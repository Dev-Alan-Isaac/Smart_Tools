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
    /// System Monitor - UI only, laid out like Task Manager's Performance tab:
    /// a list of sections (PC, CPU, Memory, each disk, adapter and GPU) and the
    /// chosen one in detail. Reading Windows is in <see cref="SystemMonitorManager"/>;
    /// the sections, units and report in SystemMonitorManager.Format.cs.
    /// Readings stop while the page isn't shown.
    /// </summary>
    public partial class SystemMonitorView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DialogTitle = "System Monitor";

        private readonly SystemMonitorManager _manager = new();
        private readonly ObservableCollection<SystemMonitorSection> _sections = new();
        private readonly ObservableCollection<SystemMonitorStatRow> _live = new();
        private readonly ObservableCollection<SystemMonitorStatRow> _info = new();
        private readonly ObservableCollection<SystemMonitorBar> _cores = new();
        private readonly ObservableCollection<SystemMonitorBar> _engines = new();

        /// <summary>Per section id: the graph's history (and the second line's), plus a disk's read/write.</summary>
        private readonly Dictionary<string, (SystemMonitorManager.History Main, SystemMonitorManager.History Second)> _history = new();
        private readonly Dictionary<string, (SystemMonitorManager.History Read, SystemMonitorManager.History Write)> _transfer = new();

        /// <summary>Per section id: the last minute of temperature (CPU, GPU, disks - when a sensor was found).</summary>
        private readonly Dictionary<string, SystemMonitorManager.History> _temps = new();

        private Dictionary<string, SystemMonitorSectionData> _data = new();
        private List<SystemMonitorSectionData> _lastSections = new();
        private SystemMonitorSettings _settings = new();
        private SystemMonitorSpecs _specs = SystemMonitorSpecs.Empty;
        private SystemMonitorSnapshot? _last;
        private bool _specsLoaded;
        private string? _selectedId;
        private bool _loadedOnce;
        private bool _applying;
        private bool _syncing;
        private bool _paused;
        private bool _closed;
        private bool _optionsCollapsed;

        public SystemMonitorView()
        {
            InitializeComponent();
            SectionList.ItemsSource = _sections;
            // Its own view, so filtering out PC here doesn't touch the list on the left.
            OverviewTiles.ItemsSource = new ListCollectionView(_sections) { Filter = o => o is SystemMonitorSection { Kind: not SystemMonitorSectionKind.Pc } };
            LiveStats.ItemsSource = _live;
            InfoRows.ItemsSource = _info;
            CoreBars.ItemsSource = _cores;
            EngineBars.ItemsSource = _engines;
            _manager.SnapshotReady += ShowSnapshot;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops reading.</summary>
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
                ElevationBanner.ShowIfNotElevated();
                ApplySettings(SystemMonitorManager.LoadSettings());
                _ = LoadSpecsAsync();
            }
            if (!_paused) _manager.Start();
            UpdateLiveUi();
        }

        /// <summary>Nothing is read while the page isn't shown.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_closed) _manager.Stop();
        }

        private void ApplySettings(SystemMonitorSettings s)
        {
            _applying = true;
            try
            {
                _settings = s;
                _manager.IntervalMs = s.RefreshMs;
                foreach (var r in new[] { RadioRefresh500, RadioRefresh1000, RadioRefresh2000, RadioRefresh5000 })
                    r.IsChecked = (string)r.Tag == s.RefreshMs.ToString();
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
                SystemMonitorManager.SaveSettings(_settings);
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

        // =================================================================
        //  Refresh and pause
        // =================================================================

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

        private void UpdateLiveUi()
        {
            IconPause.Kind = _paused ? PackIconKind.Play : PackIconKind.Pause;
            TxtPause.Text = _paused ? "Resume" : "Pause";
            LiveDot.SetResourceReference(Border.BackgroundProperty, _paused ? "SidebarTextMuted" : "StatusSuccessFg");
            LiveDot.ToolTip = _paused ? "Paused" : "Live";
        }

        // =================================================================
        //  Readings → sections
        // =================================================================

        private void ShowSnapshot(SystemMonitorSnapshot s) => ShowSnapshot(s, countHistory: true);

        private void ShowSnapshot(SystemMonitorSnapshot s, bool countHistory)
        {
            if (_closed) return;
            _last = s;
            var sections = SystemMonitorManager.BuildSections(s, _specs);
            _lastSections = sections;
            _data = sections.ToDictionary(d => d.Id);

            // History first, so the list items get this reading's graphs.
            foreach (var d in sections)
            {
                if (!_history.TryGetValue(d.Id, out var h)) _history[d.Id] = h = (new SystemMonitorManager.History(), new SystemMonitorManager.History());
                if (countHistory && d.Graph != SystemMonitorGraphKind.None)
                {
                    h.Main.Add(d.Value);
                    if (d.Graph == SystemMonitorGraphKind.Rate) h.Second.Add(d.Value2 ?? 0);
                }
                if (d.TransferRead.HasValue)
                {
                    if (!_transfer.TryGetValue(d.Id, out var t)) _transfer[d.Id] = t = (new SystemMonitorManager.History(), new SystemMonitorManager.History());
                    if (countHistory)
                    {
                        t.Read.Add(d.TransferRead.Value);
                        t.Write.Add(d.TransferWrite ?? 0);
                    }
                }
                if (d.TemperatureC.HasValue)
                {
                    if (!_temps.TryGetValue(d.Id, out var th)) _temps[d.Id] = th = new SystemMonitorManager.History();
                    if (countHistory) th.Add(d.TemperatureC.Value);
                }
            }
            foreach (string gone in _history.Keys.Where(k => !_data.ContainsKey(k)).ToList())
            {
                _history.Remove(gone);
                _transfer.Remove(gone);
                _temps.Remove(gone);
            }

            // The list on the left, updated in place (selection kept).
            _syncing = true;
            try
            {
                SystemMonitorManager.SyncList(_sections, sections.Select(d => d.Id).ToList(), x => x.Id,
                    id => new SystemMonitorSection { Id = id, Kind = _data[id].Kind, Graph = _data[id].Graph });
                foreach (var item in _sections)
                {
                    var d = _data[item.Id];
                    item.Title = d.Title;
                    item.Device = d.Device;
                    item.Summary = d.Summary;
                    item.Level = d.Level;
                    item.IsWireless = d.IsWireless;
                    var h = _history[item.Id];
                    item.History = h.Main.Values();
                    item.History2 = d.Graph == SystemMonitorGraphKind.Rate ? h.Second.Values() : Array.Empty<double>();
                }
                RestoreSelection();
            }
            finally
            {
                _syncing = false;
            }

            ShowDetail(animate: false);
            if (StartingPanel.Visibility == Visibility.Visible)
            {
                StartingPanel.Visibility = Visibility.Collapsed;
                DetailScroll.Visibility = Visibility.Visible;
                UiMotion.FadeIn(DetailScroll);
            }
            BtnCopyReport.IsEnabled = BtnSaveReport.IsEnabled = sections.Count > 0;
            if (countHistory) UiMotion.Pop(LiveDot, 0.6);
        }

        /// <summary>Keeps the chosen section chosen; the first time (or if it went away, e.g. an adapter disconnected) picks the remembered kind, else PC.</summary>
        private void RestoreSelection()
        {
            var item = _sections.FirstOrDefault(x => x.Id == _selectedId)
                       ?? _sections.FirstOrDefault(x => x.Kind == _settings.Section)
                       ?? _sections.FirstOrDefault();
            if (item == null) return;
            _selectedId = item.Id;
            if (!ReferenceEquals(SectionList.SelectedItem, item)) SectionList.SelectedItem = item;
        }

        private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // While the list is being updated, a removed item can briefly clear the selection; RestoreSelection fixes it.
            if (_syncing || SectionList.SelectedItem is not SystemMonitorSection item) return;
            _selectedId = item.Id;
            _settings.Section = item.Kind;
            SaveSettings();
            ShowDetail(animate: true);
        }

        // Tiles share the row width evenly: as many columns as fit at ~260px each.
        private void OverviewPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.UniformGrid grid)
                grid.Columns = Math.Max(1, (int)((e.NewSize.Width + 14) / 274));
        }

        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: SystemMonitorSection item })
            {
                SectionList.SelectedItem = item;
                SectionList.ScrollIntoView(item);
            }
        }

        // =================================================================
        //  The chosen section
        // =================================================================

        private void ShowDetail(bool animate)
        {
            if (_selectedId == null || !_data.TryGetValue(_selectedId, out var d)) return;
            var item = _sections.FirstOrDefault(x => x.Id == _selectedId);
            if (item == null) return;
            DetailPanel.DataContext = item;

            // ---- Graph ----
            bool hasGraph = d.Graph != SystemMonitorGraphKind.None;
            GraphBlock.Visibility = hasGraph ? Visibility.Visible : Visibility.Collapsed;
            OverviewBlock.Visibility = d.Kind == SystemMonitorSectionKind.Pc ? Visibility.Visible : Visibility.Collapsed;
            if (hasGraph)
            {
                bool rate = d.Graph == SystemMonitorGraphKind.Rate;
                TxtGraphCaption.Text = d.GraphCaption;
                GraphMain.Maximum = item.GraphMaximum;
                GraphMain.Values = item.History;
                GraphMain.Values2 = rate ? item.History2 : null;
                TxtGraphMax.Text = rate ? SystemMonitorManager.FormatBitRate(GraphMain.ScaleMaximum(item.History, item.History2)) : "100%";
                RateLegend.Visibility = rate ? Visibility.Visible : Visibility.Collapsed;
                GraphMain.SetResourceReference(SystemMonitorSparkline.StrokeProperty, d.Level switch
                {
                    SystemMonitorLevel.Hot => "StatusErrorFg",
                    SystemMonitorLevel.Warm => "StatusWarningFg",
                    _ => "AccentColor",
                });

                bool disk = _transfer.TryGetValue(d.Id, out var t);
                TransferBlock.Visibility = disk ? Visibility.Visible : Visibility.Collapsed;
                if (disk)
                {
                    GraphTransfer.Values = t.Read.Values();
                    GraphTransfer.Values2 = t.Write.Values();
                    TxtTransferMax.Text = SystemMonitorManager.FormatByteRate(GraphTransfer.ScaleMaximum(GraphTransfer.Values, GraphTransfer.Values2));
                }
            }

            // ---- Temperature (CPU, GPU, disks - only when a sensor was found) ----
            bool hasTemp = d.TemperatureC.HasValue && _temps.ContainsKey(d.Id);
            TempBlock.Visibility = hasTemp ? Visibility.Visible : Visibility.Collapsed;
            if (hasTemp)
            {
                GraphTemp.Values = _temps[d.Id].Values();
                TxtTempNow.Text = SystemMonitorManager.FormatTemp(d.TemperatureC);
                TxtTempMax.Text = SystemMonitorManager.FormatTemp(GraphTemp.ScaleMaximum(GraphTemp.Values, null));
                GraphTemp.SetResourceReference(SystemMonitorSparkline.StrokeProperty, d.Level switch
                {
                    SystemMonitorLevel.Hot => "StatusErrorFg",
                    SystemMonitorLevel.Warm => "StatusWarningFg",
                    _ => "AccentColor",
                });
            }

            // ---- CPU threads, GPU engines ----
            CoresBlock.Visibility = d.Cores.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (d.Cores.Count > 0)
            {
                SystemMonitorManager.SyncBars(_cores, d.Cores.Select((p, i) => ($"{i}", (string?)$"Logical processor {i}", p)).ToList());
                TxtCoreSummary.Text = $"busiest {d.Cores.Max():0}% · average {d.Cores.Average():0}%";
            }
            EnginesBlock.Visibility = d.Kind == SystemMonitorSectionKind.Gpu && d.Engines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (d.Engines.Count > 0)
                SystemMonitorManager.SyncBars(_engines, d.Engines.Select(x => (x.Name, (string?)null, x.Percent)).ToList());

            // ---- Figures and facts ----
            SystemMonitorManager.SyncStats(_live, d.Live);
            SystemMonitorManager.SyncStats(_info, d.Info);
            TxtInfoLoading.Visibility = !_specsLoaded && d.Info.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            InfoBlock.Visibility = d.Info.Count > 0 || !_specsLoaded ? Visibility.Visible : Visibility.Collapsed;

            if (animate)
            {
                UiMotion.FadeIn(DetailPanel);
                UiMotion.Pop(HeaderChip, 0.85);
            }
        }

        // =================================================================
        //  Specs and the report
        // =================================================================

        private async Task LoadSpecsAsync()
        {
            try
            {
                _specs = await SystemMonitorManager.ReadSpecsAsync();
            }
            catch (Exception ex)
            {
                // WMI can fail in many ways; the live readings still work.
                Log.Info($"System Monitor: specs failed: {ex.Message}");
            }
            if (_closed) return;
            _specsLoaded = true;
            if (_last != null) ShowSnapshot(_last, countHistory: false);
        }

        private string Report() => SystemMonitorManager.BuildReport(_lastSections, DateTime.Now);

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

        /// <summary>Dated name, and a taken name gets " (2)": a saved report never replaces another.</summary>
        private void BtnSaveReport_Click(object sender, RoutedEventArgs e)
        {
            string text = Report();
            var dialog = new SaveFileDialog
            {
                Title = "Save system report",
                Filter = "Text file (*.txt)|*.txt",
                FileName = SystemMonitorManager.ReportFileName(DateTime.Now),
                AddExtension = true,
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog() != true) return;
            string path = OutputFolderManager.UniquePath(dialog.FileName);
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                    writer.Write(text);
                string note = path != dialog.FileName ? $"\n\n{Path.GetFileName(dialog.FileName)} already exists, so it was kept and this report got its own name." : "";
                if (MessageBox.Show($"Saved to:\n{path}{note}\n\nOpen it now?", DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                MessageBox.Show("Couldn't save the report: " + ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
