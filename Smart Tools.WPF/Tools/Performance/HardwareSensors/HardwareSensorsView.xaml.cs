using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Hardware Sensors - UI only. Reading LibreHardwareMonitor is in
    /// <see cref="HardwareSensorsManager"/>; units, levels, headline picks and the
    /// parts list in HardwareSensorsManager.Format.cs. Lists update in place
    /// every refresh. Reading stops while the page isn't shown.
    /// </summary>
    public partial class HardwareSensorsView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_hardware-sensors_Name", "Hardware Sensors");

        private readonly HardwareSensorsManager _manager = new();
        private readonly ObservableCollection<HardwarePartItem> _parts = new();
        private readonly ObservableCollection<HardwareHeadlineTile> _tiles = new();
        private readonly ObservableCollection<HardwareSensorGroup> _groups = new();
        private readonly Dictionary<string, HardwareSensorRow> _rows = new();

        /// <summary>Per "part id|kind": the headline's last minute (kept for every part, so switching shows history).</summary>
        private readonly Dictionary<string, HardwareSensorsManager.History> _history = new();

        private Dictionary<string, HardwarePartData> _data = new();
        private List<HardwarePartData> _lastParts = new();
        private HardwareSnapshot? _last;
        private HardwareSensorsSettings _settings = new();
        private string? _selectedId;
        private string _shownSignature = "";
        private string? _shownPartId;
        private bool _loadedOnce;
        private bool _applying;
        private bool _syncing;
        private bool _paused;
        private bool _closed;
        private bool _optionsCollapsed;
        private readonly bool _isAdmin = HardwareSensorsManager.IsAdministrator();
        private bool? _driverInstalled;

        public HardwareSensorsView()
        {
            InitializeComponent();
            PartList.ItemsSource = _parts;
            HeadlineTiles.ItemsSource = _tiles;
            SensorGroups.ItemsSource = _groups;
            OverviewTiles.ItemsSource = new ListCollectionView(_parts) { Filter = o => o is HardwarePartItem { Part: not HardwarePartKind.Pc } };
            _manager.SnapshotReady += ShowSnapshot;
            _pausedTimer.Tick += (_, _) => UpdatePausedNote();
        }

        /// <summary>Called from MainWindow.Window_Closing: stops reading and closes the sensor library.</summary>
        public void CancelActiveRun()
        {
            _closed = true;
            _manager.Dispose();
        }

        // =================================================================
        //  Lifetime and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_closed) return;
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ApplySettings(HardwareSensorsManager.LoadSettings());
                SetUpBanner();
            }
            _window = Window.GetWindow(this);
            if (_window != null)
            {
                _window.StateChanged += Window_StateChanged;
                _window.IsVisibleChanged += Window_IsVisibleChanged;
            }
            if (!_paused && WindowIsShown()) _manager.Start();
            UpdateLiveUi();
        }

        private Window? _window;

        private bool WindowIsShown() => _window == null || (_window.IsVisible && _window.WindowState != WindowState.Minimized);

        private void Window_StateChanged(object? sender, EventArgs e) => ApplyWindowVisibility();

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => ApplyWindowVisibility();

        /// <summary>Minimised or hidden to the tray: stop reading (and close the sensor library); shown again: carry on unless paused.</summary>
        private void ApplyWindowVisibility()
        {
            if (_closed) return;
            if (!WindowIsShown()) _manager.Stop();
            else if (!_paused && IsLoaded) _manager.Start();
        }

        /// <summary>Nothing is read while the page isn't shown (the library is closed too).</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_window != null)
            {
                _window.StateChanged -= Window_StateChanged;
                _window.IsVisibleChanged -= Window_IsVisibleChanged;
                _window = null;
            }
            if (!_closed) _manager.Stop();
        }

        private void ApplySettings(HardwareSensorsSettings s)
        {
            _applying = true;
            try
            {
                _settings = s;
                _manager.IntervalMs = s.RefreshMs;
                foreach (var r in RefreshChips.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == s.RefreshMs.ToString();
                RadioFahrenheit.IsChecked = s.Fahrenheit;
                RadioCelsius.IsChecked = !s.Fahrenheit;
            }
            finally
            {
                _applying = false;
            }
        }

        private void SaveSettings()
        {
            if (_applying || !_loadedOnce || _closed) return;
            try
            {
                HardwareSensorsManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a convenience - never worth an error
            }
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
            if (BtnCollapseOptions.Content is StackPanel header)
                header.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_applying || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int ms)) return;
            _settings.RefreshMs = ms;
            _manager.IntervalMs = ms;
            SaveSettings();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            _paused = !_paused;
            if (_paused) _manager.Stop();
            else _manager.Start();
            UpdateLiveUi();
        }

        private readonly DispatcherTimer _pausedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private DateTime? _lastTakenUtc;

        /// <summary>While paused: "last reading 12 s ago - the graphs show the last 60 readings (about 30 s)". The age keeps counting.</summary>
        private void UpdatePausedNote()
        {
            if (!_paused) return;
            var age = _lastTakenUtc is DateTime taken ? DateTime.UtcNow - taken : TimeSpan.Zero;
            var window = TimeSpan.FromMilliseconds(HardwareSensorsManager.HistoryLength * Math.Max(250, _settings.RefreshMs));
            string Span(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00} min" : $"{Math.Max(0, (int)t.TotalSeconds)} s";
            TxtPausedNote.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Loc.T("Str_HardwareSensors_PausedNote", "Paused · last reading {0} ago · the graphs show the last {1} readings (about {2})"),
                Span(age), HardwareSensorsManager.HistoryLength, Span(window));
            TxtPausedNote.Visibility = Visibility.Visible;
        }

        private void UpdateLiveUi()
        {
            IconPause.Kind = _paused ? PackIconKind.Play : PackIconKind.Pause;
            TxtPause.Text = _paused ? Loc.T("Str_MicRecorder_Resume", "Resume") : Loc.T("Str_MicRecorder_Pause", "Pause");
            LiveDot.SetResourceReference(Border.BackgroundProperty, _paused ? "SidebarTextMuted" : "StatusSuccessFg");
            LiveDot.ToolTip = _paused ? Loc.T("Str_SystemMonitor_Paused", "Paused") : Loc.T("Str_HardwareSensors_Live", "Live");
            _pausedTimer.Stop();
            if (_paused)
            {
                UpdatePausedNote();
                _pausedTimer.Start();
            }
            else
            {
                TxtPausedNote.Visibility = Visibility.Collapsed;
            }
        }

        private void Units_Click(object sender, RoutedEventArgs e)
        {
            if (_applying) return;
            _settings.Fahrenheit = RadioFahrenheit.IsChecked == true;
            SaveSettings();
            if (_last != null) ShowSnapshot(_last, countHistory: false);
        }

        // =================================================================
        //  Readings → parts
        // =================================================================

        private void ShowSnapshot(HardwareSnapshot s) => ShowSnapshot(s, countHistory: true);

        private void ShowSnapshot(HardwareSnapshot s, bool countHistory)
        {
            _lastTakenUtc = s.TakenUtc;
            if (_closed) return;
            _last = s;
            var parts = HardwareSensorsManager.BuildParts(s.Readings, _settings.Fahrenheit);
            _lastParts = parts;
            _data = parts.ToDictionary(p => p.Id);

            // Headline history for every part (temperatures kept in °C so switching units doesn't bend the graph).
            if (countHistory)
            {
                foreach (var p in parts)
                    foreach (var h in p.Headlines)
                    {
                        string key = HistoryKey(p.Id, h);
                        if (!_history.TryGetValue(key, out var hist)) _history[key] = hist = new HardwareSensorsManager.History();
                        hist.Add(h.Raw ?? 0);
                        _rec.Add(key, h.Raw ?? 0);
                    }
            }

            _syncing = true;
            try
            {
                HardwareSensorsManager.SyncList(_parts, parts.Select(p => p.Id).ToList(), x => x.Id, id => new HardwarePartItem { Id = id, Part = _data[id].Part });
                foreach (var item in _parts)
                {
                    var d = _data[item.Id];
                    item.Name = d.Part == HardwarePartKind.Pc ? TryFindResource("Str_HardwareSensors_ThisPc") as string ?? d.Name : d.Name;
                    item.PartTitle = HardwareSensorsManager.PartTitle(d.Part);
                    item.Summary = d.Summary;
                    item.Level = d.Level;
                    item.Headlines = d.Headlines;
                }
                RestoreSelection();
            }
            finally
            {
                _syncing = false;
            }

            bool any = parts.Count > 0;
            DetailScroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            EmptyPanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            if (!any)
            {
                EmptyProgress.Visibility = Visibility.Collapsed;
                TxtEmptyTitle.Text = s.Error != null ? Loc.T("Str_HardwareSensors_CantReadTitle", "Couldn't read the sensors") : Loc.T("Str_HardwareSensors_NoSensorsTitle", "No sensors found");
                TxtEmptyHint.Text = s.Error ?? Loc.T("Str_HardwareSensors_NoSensorsHint", "This PC's hardware didn't report any sensors. System Monitor still shows usage for every part.");
            }
            ShowDetail(animate: false);
            _driverInstalled ??= HardwareSensorsManager.IsSensorDriverInstalled();
            string? note = HardwareSensorsManager.CoverageNote(parts, _isAdmin, _driverInstalled.Value);
            TxtCoverage.Text = note ?? "";
            // The banner already says it; the sidebar card is the fallback once the banner is hidden.
            CoverageCard.Visibility = note == null || ElevationBanner.Visibility == Visibility.Visible || AdminBanner.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            BtnCopyReport.IsEnabled = BtnSaveReport.IsEnabled = any;
            if (countHistory) UiMotion.Pop(LiveDot, 0.6);
        }

        /// <summary>Keeps the chosen part chosen; first time (or if it went away) picks the remembered kind, else the first.</summary>
        private void RestoreSelection()
        {
            var item = _parts.FirstOrDefault(x => x.Id == _selectedId)
                       ?? _parts.FirstOrDefault(x => x.Part == _settings.Part)
                       ?? _parts.FirstOrDefault();
            if (item == null) return;
            _selectedId = item.Id;
            if (!ReferenceEquals(PartList.SelectedItem, item)) PartList.SelectedItem = item;
        }

        private void PartList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || PartList.SelectedItem is not HardwarePartItem item) return;
            _selectedId = item.Id;
            _settings.Part = item.Part;
            SaveSettings();
            ShowDetail(animate: true);
        }

        // =================================================================
        //  The chosen part
        // =================================================================

        /// <summary>Two headlines of one part can share a kind (the PC page has a CPU and a GPU load), so the label is part of the key.</summary>
        private readonly PerfRecorder _rec = new();

        private static string HistoryKey(string partId, HardwareHeadline h) => partId + "|" + h.Kind + "|" + h.Label;

        // Tiles share the row width evenly: as many columns as fit at ~260px each.
        private void OverviewPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.UniformGrid grid)
                grid.Columns = Math.Max(1, (int)((e.NewSize.Width + 14) / 274));
        }

        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: HardwarePartItem item })
            {
                PartList.SelectedItem = item;
                PartList.ScrollIntoView(item);
            }
        }

        private void ShowDetail(bool animate)
        {
            if (_selectedId == null || !_data.TryGetValue(_selectedId, out var d)) return;
            var item = _parts.FirstOrDefault(x => x.Id == _selectedId);
            if (item == null) return;
            DetailPanel.DataContext = item;
            TxtPartKind.Text = HardwareSensorsManager.PartTitle(d.Part);
            bool isPc = d.Part == HardwarePartKind.Pc;
            int sensorCount = isPc ? _lastParts.Where(p => p.Part != HardwarePartKind.Pc).Sum(p => p.SensorCount) : d.SensorCount;
            TxtSensorCount.Text = sensorCount == 1 ? "1 sensor" : $"{sensorCount} sensors";
            OverviewBlock.Visibility = isPc ? Visibility.Visible : Visibility.Collapsed;

            // ---- Headline tiles: same figures → update in place; otherwise rebuild ----
            bool sameTiles = _shownPartId == d.Id && _tiles.Select(t => t.Label).SequenceEqual(d.Headlines.Select(h => h.Label));
            if (!sameTiles)
            {
                _tiles.Clear();
                foreach (var h in d.Headlines) _tiles.Add(new HardwareHeadlineTile { Kind = h.Kind, Label = h.Label });
            }
            for (int i = 0; i < d.Headlines.Count; i++)
            {
                var h = d.Headlines[i];
                _tiles[i].ValueText = h.ValueText;
                _tiles[i].Level = h.Level;
                _tiles[i].History = _history.TryGetValue(HistoryKey(d.Id, h), out var hist) ? hist.Values() : Array.Empty<double>();
            }
            HeadlineTiles.Visibility = d.Headlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // ---- Sensor groups: same sensors → values update in place; otherwise rebuild ----
            string signature = HardwareSensorsManager.GroupsSignature(d);
            if (_shownPartId != d.Id || signature != _shownSignature)
            {
                _groups.Clear();
                _rows.Clear();
                foreach (var g in d.Groups)
                {
                    var group = new HardwareSensorGroup { Kind = g.Kind, Title = g.Title };
                    foreach (var r in g.Rows)
                    {
                        var row = new HardwareSensorRow { Id = r.Id, Name = r.Name };
                        group.Rows.Add(row);
                        _rows[r.Id] = row;
                    }
                    _groups.Add(group);
                }
                _shownSignature = signature;
            }
            foreach (var r in d.Groups.SelectMany(g => g.Rows))
            {
                if (!_rows.TryGetValue(r.Id, out var row)) continue;
                row.ValueText = r.ValueText;
                row.MinText = r.MinText;
                row.MaxText = r.MaxText;
                row.Level = r.Level;
            }
            _shownPartId = d.Id;

            if (animate)
            {
                UiMotion.FadeIn(DetailPanel);
                UiMotion.Pop(HeaderChip, 0.85);
                DetailScroll.ScrollToTop();
            }
        }

        // =================================================================
        //  Elevation / driver banner
        // =================================================================

        /// <summary>
        /// Without admin rights (and the PawnIO driver for CPU/motherboard) many
        /// sensors are missing or read 0 - say so up front, like Drive Health.
        /// Neither can change without a restart, so this runs once.
        /// </summary>
        private void SetUpBanner()
        {
            _driverInstalled ??= HardwareSensorsManager.IsSensorDriverInstalled();
            bool driver = _driverInstalled.Value;
            if (_isAdmin && driver) return;

            if (!_isAdmin)
            {
                AdminBanner.MessageKey = driver ? "Str_HardwareSensors_AdminWarning" : "Str_HardwareSensors_AdminAndDriverWarning";
                AdminBanner.ShowIfNotElevated();
                return;
            }

            // Elevated but the PawnIO driver is missing: say so, with the download.
            TxtBannerTitle.SetResourceReference(TextBlock.TextProperty, "Str_HardwareSensors_DriverTitle");
            TxtBannerText.SetResourceReference(TextBlock.TextProperty, "Str_HardwareSensors_DriverWarning");
            TxtBannerAction.SetResourceReference(TextBlock.TextProperty, "Str_HardwareSensors_GetDriver");
            BtnBannerAction.SetResourceReference(ToolTipProperty, "Str_HardwareSensors_GetDriverTooltip");
            IconBannerAction.Kind = PackIconKind.Download;
            UiMotion.FadeVisibility(ElevationBanner, true);
        }

        private void BtnBannerAction_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(Loc.T("Str_MB_CouldnTOpenThe4", "Couldn't open the browser. The driver is at https://pawnio.eu"), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnDismissBanner_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.FadeVisibility(ElevationBanner, false);
            // Bring back the short sidebar note in its place.
            if (!string.IsNullOrEmpty(TxtCoverage.Text)) CoverageCard.Visibility = Visibility.Visible;
        }

        // =================================================================
        //  Report
        // =================================================================

        private string Report() => HardwareSensorsManager.BuildReport(_lastParts, DateTime.Now);

        private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(Report());
                UiMotion.Pop(BtnCopyReport, 0.85);
                IconCopy.Kind = PackIconKind.Check;
                var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                reset.Tick += (_, _) =>
                {
                    reset.Stop();
                    IconCopy.Kind = PackIconKind.ContentCopy;
                };
                reset.Start();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // another program has the clipboard open - a second click works
            }
        }

        /// <summary>Every part with the readings kept since the page opened: one chart per kind of headline (temperatures together, loads together...).</summary>
        private PerfReportData BuildPerfReport()
        {
            string[] palette = { PerfReportBuilder.Blue, PerfReportBuilder.Orange, PerfReportBuilder.Green, PerfReportBuilder.Red, "#8b5cf6", "#14b8a6" };
            bool f = _settings.Fahrenheit;
            var sections = new List<PerfSection>();
            foreach (var part in _lastParts)
            {
                var rows = new List<PerfRow>();
                foreach (var h in part.Headlines) rows.Add(new PerfRow(h.Label, h.ValueText));
                foreach (var g in part.Groups)
                    foreach (var r in g.Rows)
                        rows.Add(new PerfRow(g.Title + " - " + r.Name, r.ValueText + (r.MinText != "–" || r.MaxText != "–" ? $"   ({r.MinText} - {r.MaxText})" : "")));

                var charts = new List<PerfChart>();
                foreach (var group in part.Headlines.Where(h => h.Raw.HasValue).GroupBy(h => h.Kind))
                {
                    var kind = group.Key;
                    var series = group.Select((h, i) => new PerfSeries(h.Label, _rec.Get(HistoryKey(part.Id, h)).Select(v => kind == HardwareSensorKind.Temperature && f ? v * 9 / 5 + 32 : v).ToList(), palette[i % palette.Length])).ToList();
                    charts.Add(new PerfChart(HardwareSensorsManager.PartTitle(part.Part) + " - " + group.First().Label,
                        series, v => HardwareSensorsManager.FormatValue(kind, (float)(kind == HardwareSensorKind.Temperature && f ? (v - 32) * 5 / 9 : v), f),
                        kind == HardwareSensorKind.Load ? 100 : null));
                }
                string name = part.Part == HardwarePartKind.Pc ? Loc.T("Str_HardwareSensors_ThisPc", part.Name) : part.Name;
                sections.Add(new PerfSection(name, HardwareSensorsManager.PartTitle(part.Part), rows, charts));
            }
            return new PerfReportData(Loc.T("Str_HardwareSensors_ReportTitle", "Hardware sensors"), DateTime.Now, Math.Max(0.25, _settings.RefreshMs / 1000.0),
                Loc.T("Str_HardwareSensors_ReportNote", "Charts show the readings taken while this page was open."), sections);
        }

        /// <summary>Dated name, and a taken name gets " (2)": a saved report never replaces another. HTML and PDF carry the charts; text is the plain list.</summary>
        private void BtnSaveReport_Click(object sender, RoutedEventArgs e)
        {
            string kind = (sender as System.Windows.Controls.MenuItem)?.Tag as string ?? "html";   // the menu item picks the format; the dialog still lets it change
            var dialog = new SaveFileDialog
            {
                Title = Loc.T("Str_HardwareSensors_SaveReportTitle", "Save hardware sensors report"),
                Filter = Loc.T("Str_SystemMonitor_ReportFilterHtml", "Web page with charts (*.html)|*.html") + "|" + Loc.T("Str_SystemMonitor_ReportFilterPdf", "PDF with charts (*.pdf)|*.pdf") + "|" + Loc.T("Str_SystemMonitor_ReportFilterTxt", "Text file (*.txt)|*.txt"),
                FileName = Path.ChangeExtension(HardwareSensorsManager.ReportFileName(DateTime.Now), kind),
                FilterIndex = kind == "pdf" ? 2 : kind == "txt" ? 3 : 1,
                AddExtension = true,
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog() != true) return;
            string path = OutputFolderManager.UniquePath(dialog.FileName);
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".pdf") AtomicFile.WriteAllBytes(path, PerfReportBuilder.ToPdf(BuildPerfReport()));
                else if (ext == ".html" || ext == ".htm") AtomicFile.WriteAllText(path, PerfReportBuilder.ToHtml(BuildPerfReport()), new System.Text.UTF8Encoding(false));
                else AtomicFile.WriteAllText(path, Report(), new System.Text.UTF8Encoding(false));
                string note = path != dialog.FileName ? "\n\n" + Loc.F("Str_HardwareSensors_KeptExisting", "{0} already exists, so it was kept and this report got its own name.", Path.GetFileName(dialog.FileName)) : "";
                if (MessageBox.Show(Loc.F("Str_MB_SavedToNN", "Saved to:\n{0}{1}\n\nOpen it now?", path, note), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                MessageBox.Show(Loc.T("Str_MB_CouldnTSaveThe2", "Couldn't save the report: ") + ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
