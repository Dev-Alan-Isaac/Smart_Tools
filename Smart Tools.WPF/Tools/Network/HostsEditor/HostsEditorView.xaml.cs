using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Hosts File Editor - UI only. Parsing, checking, backups and saving are in <see cref="HostsEditorManager"/>.
    /// Edits stay in the grid until Save; every save keeps a copy of the previous file.
    /// </summary>
    public partial class HostsEditorView : UserControl
    {
        private const string DialogTitle = "Hosts File Editor";

        private readonly HostsEditorManager _hosts = new();
        private readonly ObservableCollection<HostsRow> _shown = new();
        private readonly bool _viewConstructed;
        private bool _loadedOnce, _dirty;

        public HostsEditorView()
        {
            InitializeComponent();
            GridHosts.ItemsSource = _shown;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing. Nothing is held open; unsaved edits are simply not written.</summary>
        public void CancelActiveRun() { }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            AdminBanner.Visibility = HostsEditorManager.IsAdministrator ? Visibility.Collapsed : Visibility.Visible;
            if (_loadedOnce) return;
            _loadedOnce = true;
            LoadFile();
        }

        // =================================================================
        //  Load, list
        // =================================================================

        private void LoadFile(string? path = null)
        {
            try
            {
                _hosts.Load(path);
                _dirty = path != null;   // a backup is only a proposal until it is saved
                SetStatus(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus(string.Format(T("Str_Hosts_LoadFailed", "Couldn't read the file: {0}"), ex.Message), "StatusErrorFg");
            }
            Refresh();
        }

        private void Refresh()
        {
            foreach (var row in _shown) row.PropertyChanged -= Row_Changed;
            _shown.Clear();

            string query = TxtSearch.Text.Trim();
            foreach (var row in _hosts.Entries)
            {
                ApplyProblem(row);
                if (query.Length > 0 && !Matches(row, query)) continue;
                row.PropertyChanged += Row_Changed;
                _shown.Add(row);
            }

            bool any = _hosts.Entries.Any();
            PanelEmpty.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtEmptyTitle.Text = any ? T("Str_Hosts_NoMatches", "Nothing matches") : T("Str_Hosts_EmptyTitle", "No entries yet");
            TxtEmptyHint.Text = any ? T("Str_Hosts_NoMatchesHint", "Try another search.") : T("Str_Hosts_EmptyHint", "Add an address and the names that should point to it.");
            UpdateSummary();
            BtnSave.IsEnabled = _dirty;
        }

        private static bool Matches(HostsRow row, string query) =>
            row.Address.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            row.Names.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            row.Comment.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        private void UpdateSummary()
        {
            int total = _hosts.Entries.Count(), on = _hosts.Entries.Count(r => r.IsEnabled);
            TxtCount.Text = string.Format(T("Str_Hosts_Count", "{0} entries, {1} on"), total, on);
        }

        private void ApplyProblem(HostsRow row)
        {
            row.Problem = HostsEditorManager.Validate(row);
            row.ProblemText = row.Problem switch
            {
                HostsProblem.BadAddress => T("Str_Hosts_ProblemAddress", "That isn't a valid IP address."),
                HostsProblem.NoNames => T("Str_Hosts_ProblemNoNames", "Add at least one host name."),
                HostsProblem.BadName => T("Str_Hosts_ProblemName", "Host names can only use letters, numbers, dots, hyphens, and underscores."),
                _ => "",
            };
        }

        private void Row_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not HostsRow row || e.PropertyName is not (nameof(HostsRow.Address) or nameof(HostsRow.Names) or nameof(HostsRow.Comment) or nameof(HostsRow.IsEnabled))) return;
            _dirty = true;
            BtnSave.IsEnabled = true;
            if (e.PropertyName is nameof(HostsRow.Address) or nameof(HostsRow.Names)) ApplyProblem(row);
            UpdateSummary();
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) Refresh();
        }

        // =================================================================
        //  Add, delete
        // =================================================================

        private void AddBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnAdd_Click(sender, e);
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var row = new HostsRow { Kind = HostsRowKind.Entry, Address = TxtAddAddress.Text, Names = TxtAddNames.Text, Comment = TxtAddComment.Text };
            ApplyProblem(row);
            if (row.HasProblem)
            {
                SetStatus(row.ProblemText, "StatusErrorFg");
                (row.Problem == HostsProblem.BadAddress ? TxtAddAddress : TxtAddNames).Focus();
                return;
            }

            _hosts.Add(row);
            _dirty = true;
            TxtAddNames.Clear();
            TxtAddComment.Clear();
            TxtSearch.Clear();
            SetStatus(null);
            Refresh();
            GridHosts.ScrollIntoView(row);
            TxtAddNames.Focus();
        }

        private void BtnDeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not HostsRow row) return;
            GridHosts.CancelEdit();
            _hosts.Remove(row);
            _dirty = true;
            Refresh();
        }

        // =================================================================
        //  Save, reload, backups, DNS
        // =================================================================

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            GridHosts.CommitEdit(DataGridEditingUnit.Row, true);
            switch (_hosts.Save(out string? error))
            {
                case HostsSaveResult.Saved:
                    _dirty = false;
                    BtnSave.IsEnabled = false;
                    SetStatus(T("Str_Hosts_Saved", "Saved. A copy of the previous file was kept."), "StatusSuccessFg");
                    break;
                case HostsSaveResult.HasProblems:
                    SetStatus(T("Str_Hosts_FixProblems", "Fix the entries marked with a red icon before saving."), "StatusErrorFg");
                    break;
                case HostsSaveResult.NeedsAdministrator:
                    AdminBanner.Visibility = Visibility.Visible;
                    SetStatus(T("Str_Hosts_NeedsAdmin", "Windows won't let this app change the hosts file without administrator rights."), "StatusErrorFg");
                    break;
                default:
                    SetStatus(string.Format(T("Str_Hosts_SaveFailed", "Couldn't save the hosts file: {0}"), error), "StatusErrorFg");
                    break;
            }
        }

        private bool ConfirmDiscard() =>
            !_dirty || MessageBox.Show(T("Str_Hosts_Discard", "Discard the changes you haven't saved?"), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            if (ConfirmDiscard()) LoadFile();
        }

        private void BtnBackups_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = BtnBackups, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var backups = HostsEditorManager.ListBackups();
            if (backups.Count == 0)
                menu.Items.Add(new MenuItem { Header = T("Str_Hosts_NoBackups", "No backups yet"), IsEnabled = false });
            foreach (string path in backups)
            {
                var item = new MenuItem { Header = BackupLabel(path), Tag = path };
                item.Click += BackupItem_Click;
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private static string BackupLabel(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path).Replace("hosts-", "");
            return DateTime.TryParseExact(name, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
                ? when.ToString("g", CultureInfo.CurrentCulture)
                : name;
        }

        private void BackupItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.Tag is not string path || !ConfirmDiscard()) return;
            LoadFile(path);
            SetStatus(T("Str_Hosts_BackupLoaded", "Backup loaded. Save to put it back in place."), "TextSecondary");
        }

        private async void BtnFlush_Click(object sender, RoutedEventArgs e)
        {
            BtnFlush.IsEnabled = false;
            try
            {
                bool ok = await HostsEditorManager.FlushDnsAsync();
                SetStatus(ok ? T("Str_Hosts_DnsFlushed", "DNS cache cleared.") : T("Str_Hosts_DnsFailed", "Couldn't clear the DNS cache."), ok ? "StatusSuccessFg" : "StatusErrorFg");
            }
            finally
            {
                BtnFlush.IsEnabled = true;
            }
        }

        private void SetStatus(string? text, string colorKey = "TextSecondary")
        {
            TxtStatus.Text = text ?? "";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            TxtStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Relaunches Smart Tools elevated (UAC prompt) and closes this instance - asks first.</summary>
        private void BtnRestartAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(T("Str_Hosts_RestartConfirm", "Restart Smart Tools as administrator? Unsaved changes in the editor are lost."), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (Window.GetWindow(this) is MainWindow main && !main.PrepareForRestart()) return;

            switch (AppRestartManager.StartElevatedCopy(out string? error))
            {
                case AppRestartResult.Started:
                    Application.Current.Shutdown();
                    break;
                case AppRestartResult.Failed:
                    MessageBox.Show(string.Format(T("Str_Hosts_RestartFailed", "Couldn't restart as administrator: {0}"), error), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
                // Declined: UAC said no - stay as we are.
            }
        }
    }
}
