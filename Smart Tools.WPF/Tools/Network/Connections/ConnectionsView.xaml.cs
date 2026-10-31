using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Connections - UI only. Reading, outside or local, filtering and the CSV are in <see cref="ConnectionsManager"/>.</summary>
    public partial class ConnectionsView : UserControl
    {
        private static readonly int[] RefreshChoices = [0, 2, 5, 10, 30];

        private readonly ConnectionsManager _manager = new();
        private readonly ConnectionsSettings _settings = ConnectionsManager.LoadSettings();
        private readonly ObservableCollection<ConnectionRow> _rows = new();
        private readonly Dictionary<string, ConnectionRow> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _hostNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _asking = new(StringComparer.Ordinal);
        private readonly ICollectionView _view;
        private readonly bool _viewConstructed;
        private CancellationTokenSource? _lookups;
        private bool _loaded;
        private int _shownApps = -1, _shownActive = -1;

        public ConnectionsView()
        {
            InitializeComponent();

            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = o => o is ConnectionRow r && ConnectionsManager.Matches(r, _settings.Scope, _settings.HideListening, TxtSearch.Text);
            _view.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.AppName), ListSortDirection.Ascending));
            _view.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.RemoteAddress), ListSortDirection.Ascending));
            GridConnections.ItemsSource = _view;

            FillRefreshChoices();
            RadioOutside.IsChecked = _settings.Scope == ConnectionScope.Outside;
            RadioLocal.IsChecked = _settings.Scope == ConnectionScope.Local;
            RadioAll.IsChecked = _settings.Scope == ConnectionScope.All;
            ChkHideListening.IsChecked = _settings.HideListening;
            ChkLookUp.IsChecked = _settings.LookUpNames;
            CmbRefresh.SelectedIndex = Math.Max(0, Array.IndexOf(RefreshChoices, _settings.RefreshSeconds));

            _manager.Updated += OnUpdated;
            _viewConstructed = true;
            UpdateEmpty();
        }

        /// <summary>Called from MainWindow.Window_Closing: stops reading and any name lookups.</summary>
        public void CancelActiveRun()
        {
            StopReading();
            _manager.Dispose();
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void FillRefreshChoices()
        {
            foreach (int seconds in RefreshChoices)
                CmbRefresh.Items.Add(new ComboBoxItem
                {
                    Tag = seconds,
                    Content = seconds == 0 ? T("Str_Connections_Paused", "Paused") : string.Format(T("Str_Connections_EverySeconds", "Every {0} s"), seconds),
                });
        }

        // =================================================================
        //  Reading (only while the page is showing)
        // =================================================================

        private void Root_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (IsVisible) StartReading();
            else StopReading();
        }

        private void StartReading()
        {
            if (_settings.LookUpNames) _lookups ??= new CancellationTokenSource();
            if (_settings.RefreshSeconds > 0) _manager.Start(_settings.RefreshSeconds);
            else Task.Run(_manager.ReadNow);   // paused: one reading so the page isn't empty
        }

        private void StopReading()
        {
            _manager.Stop();
            _lookups?.Cancel();
            _lookups = null;
            _asking.Clear();
        }

        private void OnUpdated(IReadOnlyList<ConnectionInfo> list, IReadOnlySet<string> added)
        {
            _loaded = true;
            var seen = new HashSet<string>(list.Count, StringComparer.Ordinal);
            // No _view.DeferRefresh() here: WPF throws when the collection changes while the view's refresh is deferred.
            foreach (var info in list)
            {
                if (!seen.Add(info.Key)) continue;   // the table can list the same endpoint twice in one reading
                ConnectionRow row;
                if (_byKey.TryGetValue(info.Key, out var existing))
                {
                    if (existing.Info == info)
                    {
                        existing.IsNew = false;
                        continue;
                    }
                    _rows.Remove(existing);   // its state changed: a fresh row keeps the sort and filter honest
                    row = new ConnectionRow(info) { Host = existing.Host };
                }
                else
                {
                    row = new ConnectionRow(info) { IsNew = added.Contains(info.Key) };
                }
                _byKey[info.Key] = row;
                _rows.Add(row);
            }

            foreach (var gone in _byKey.Where(p => !seen.Contains(p.Key)).ToList())
            {
                _rows.Remove(gone.Value);
                _byKey.Remove(gone.Key);
            }

            var active = ConnectionsManager.ActiveOutside(list).ToList();
            ShowCount(TxtApps, ref _shownApps, active.Select(c => c.Pid).Distinct().Count());
            ShowCount(TxtActive, ref _shownActive, active.Count);
            UpdateEmpty();
            if (_settings.LookUpNames) LookUpHosts();
        }

        private static void ShowCount(TextBlock label, ref int shown, int value)
        {
            if (shown == value) return;
            bool first = shown < 0;
            shown = value;
            label.Text = value.ToString(CultureInfo.CurrentCulture);
            if (!first) UiMotion.Pop(label);
        }

        private void UpdateEmpty()
        {
            bool empty = _view.IsEmpty;
            EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (!empty) return;
            TxtEmpty.Text = !_loaded
                ? T("Str_Connections_Waiting", "Reading connections...")
                : _rows.Count == 0 || TxtSearch.Text.Trim().Length > 0
                    ? T("Str_Connections_NoMatch", "No connections match")
                    : T("Str_Connections_NothingOutside", "No app is connected outside right now");
        }

        // =================================================================
        //  Host names (only when the option is on)
        // =================================================================

        private void LookUpHosts()
        {
            var token = _lookups?.Token ?? CancellationToken.None;
            if (_lookups == null) return;
            foreach (var group in _rows.Where(r => r.IsOutside && r.Host.Length == 0).GroupBy(r => r.RemoteAddress).ToList())
            {
                if (_hostNames.TryGetValue(group.Key, out string? known))
                {
                    foreach (var row in group) row.Host = known;
                    continue;
                }
                if (_asking.Add(group.Key)) _ = ResolveAsync(group.Key, token);
            }
        }

        private async Task ResolveAsync(string address, CancellationToken token)
        {
            try
            {
                string host = await _manager.LookUpHostAsync(address, token);
                _hostNames[address] = host;
                foreach (var row in _rows.Where(r => r.RemoteAddress == address)) row.Host = host;
            }
            catch (OperationCanceledException)
            {
                // the page was hidden or the option turned off: nothing to show
            }
            finally
            {
                _asking.Remove(address);
            }
        }

        // =================================================================
        //  Options
        // =================================================================

        private void Scope_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || sender is not RadioButton { Tag: string tag }) return;
            _settings.Scope = Enum.Parse<ConnectionScope>(tag);
            Save();
            Refilter();
        }

        private void ChkHideListening_Click(object sender, RoutedEventArgs e)
        {
            _settings.HideListening = ChkHideListening.IsChecked == true;
            Save();
            Refilter();
        }

        private void CmbRefresh_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || CmbRefresh.SelectedItem is not ComboBoxItem { Tag: int seconds }) return;
            _settings.RefreshSeconds = seconds;
            Save();
            if (!IsVisible) return;   // the next time the page shows, StartReading uses the new interval
            if (seconds > 0) _manager.Start(seconds);
            else _manager.Stop();   // paused: keep what is on screen
        }

        private void ChkLookUp_Click(object sender, RoutedEventArgs e)
        {
            _settings.LookUpNames = ChkLookUp.IsChecked == true;
            Save();
            if (_settings.LookUpNames)
            {
                _lookups ??= new CancellationTokenSource();
                LookUpHosts();
            }
            else
            {
                _lookups?.Cancel();
                _lookups = null;
                _asking.Clear();
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) Refilter();
        }

        private void Refilter()
        {
            _view.Refresh();
            UpdateEmpty();
        }

        private void Save() => ConnectionsManager.SaveSettings(_settings);

        // =================================================================
        //  Rows: copy, export
        // =================================================================

        /// <summary>A right-click selects the row under the pointer, like in Explorer.</summary>
        private void GridConnections_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as DependencyObject;
            while (element != null && element is not DataGridRow) element = VisualTreeHelper.GetParent(element);
            if (element is not DataGridRow row || row.IsSelected) return;
            GridConnections.SelectedItems.Clear();
            row.IsSelected = true;
        }

        private void MenuCopy_Click(object sender, RoutedEventArgs e)
        {
            var addresses = GridConnections.SelectedItems.OfType<ConnectionRow>()
                .Select(r => r.RemoteAddress).Where(a => a != "*").Distinct().ToList();
            if (addresses.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, addresses));
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // another program is using the clipboard; nothing was copied
            }
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV|*.csv",
                FileName = "connections-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) + ".csv",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                AtomicFile.WriteAllText(dialog.FileName, ConnectionsManager.ToCsv(_view.Cast<ConnectionRow>()), new UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message, T("Str_Connections_Export", "Export"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
