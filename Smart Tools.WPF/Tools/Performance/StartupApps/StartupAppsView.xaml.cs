using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Startup Apps - UI only. Finding the entries and switching them is in <see cref="StartupAppsManager"/>.
    /// The switch is the same one Task Manager uses, so both always agree.
    /// </summary>
    public partial class StartupAppsView : UserControl
    {
        private static string DialogTitle => Loc.T("Str_Tool_startup-apps_Name", "Startup Apps");

        private readonly StartupAppsManager _startup = new();
        private readonly ObservableCollection<StartupApp> _shown = new();
        private List<StartupApp> _all = new();
        private readonly bool _viewConstructed;
        private bool _loadedOnce, _scanning;

        public StartupAppsView()
        {
            InitializeComponent();
            GridApps.ItemsSource = _shown;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing. Nothing is held open.</summary>
        public void CancelActiveRun() { }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private async void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await ScanAsync();
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await ScanAsync();

        private async Task ScanAsync()
        {
            if (_scanning) return;
            _scanning = true;
            BtnRefresh.IsEnabled = false;
            try
            {
                _all = await Task.Run(_startup.Scan);
                foreach (var app in _all) app.SourceText = SourceName(app.Location);
                Refresh();
            }
            finally
            {
                BtnRefresh.IsEnabled = true;
                _scanning = false;
            }
        }

        private static string SourceName(StartupLocation location) => location switch
        {
            StartupLocation.UserRegistry => T("Str_Startup_SourceUser", "Current user"),
            StartupLocation.MachineRegistry => T("Str_Startup_SourceAll", "All users"),
            StartupLocation.MachineRegistry32 => T("Str_Startup_SourceAll32", "All users (32-bit)"),
            StartupLocation.UserFolder => T("Str_Startup_SourceFolder", "Startup folder"),
            _ => T("Str_Startup_SourceFolderAll", "Startup folder (all users)"),
        };

        private void Refresh()
        {
            _shown.Clear();
            string query = TxtSearch.Text.Trim();
            foreach (var app in _all)
            {
                if (query.Length > 0 &&
                    !app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                    !app.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                    !app.Command.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
                _shown.Add(app);
            }

            int on = _all.Count(a => a.IsEnabled);
            TxtCount.Text = string.Format(T("Str_Startup_Count", "{0} apps, {1} on"), _all.Count, on);
            PanelEmpty.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool any = _all.Count > 0;
            TxtEmptyTitle.Text = any ? T("Str_Startup_NoMatches", "Nothing matches") : T("Str_Startup_EmptyTitle", "Nothing starts with Windows");
            TxtEmptyHint.Text = any ? T("Str_Startup_NoMatchesHint", "Try another search.") : T("Str_Startup_EmptyHint", "No startup entries were found for you or for all users.");
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) Refresh();
        }

        // =================================================================
        //  Switch, show, remove
        // =================================================================

        private void ChkEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { DataContext: StartupApp app } box) return;
            bool wanted = box.IsChecked == true;
            var result = _startup.SetEnabled(app, wanted);
            if (result == StartupChangeResult.Done)
            {
                SetStatus(string.Format(T(wanted ? "Str_Startup_TurnedOn" : "Str_Startup_TurnedOff", wanted ? "{0} will start with Windows." : "{0} won't start with Windows."), app.Name), "TextSecondary");
                TxtCount.Text = string.Format(T("Str_Startup_Count", "{0} apps, {1} on"), _all.Count, _all.Count(a => a.IsEnabled));
                return;
            }
            app.IsEnabled = !wanted;   // put the switch back
            ReportFailure(result);
        }

        private void BtnShow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is StartupApp app) StartupAppsManager.ShowInFolder(app);
        }

        private async void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not StartupApp app) return;
            string question = string.Format(T("Str_Startup_RemoveConfirm", "Remove \"{0}\" from startup? The program itself is not uninstalled. To keep it and just stop it starting, turn its switch off instead."), app.Name);
            if (AppModal.Show(question, DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

            var result = _startup.Remove(app);
            if (result != StartupChangeResult.Done)
            {
                ReportFailure(result);
                return;
            }
            SetStatus(string.Format(T("Str_Startup_Removed", "{0} was removed from startup."), app.Name), "StatusSuccessFg");
            await ScanAsync();
        }

        private void ReportFailure(StartupChangeResult result)
        {
            if (result == StartupChangeResult.NeedsAdministrator)
            {
                AdminBanner.Visibility = Visibility.Visible;
                SetStatus(T("Str_Startup_NeedsAdmin", "That entry is for all users. Windows only lets an administrator change it."), "StatusErrorFg");
            }
            else
            {
                SetStatus(T("Str_Startup_Failed", "Couldn't change that entry. It may be in use or protected."), "StatusErrorFg");
            }
        }

        private void SetStatus(string? text, string colorKey)
        {
            TxtStatus.Text = text ?? "";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            TxtStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Relaunches Smart Tools elevated (UAC prompt) and closes this instance - asks first.</summary>
        private void BtnRestartAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (AppModal.Show(T("Str_Startup_RestartConfirm", "Restart Smart Tools as administrator?"), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (Window.GetWindow(this) is MainWindow main && !main.PrepareForRestart()) return;

            switch (AppRestartManager.StartElevatedCopy(out string? error))
            {
                case AppRestartResult.Started:
                    Application.Current.Shutdown();
                    break;
                case AppRestartResult.Failed:
                    AppModal.Show(string.Format(T("Str_Startup_RestartFailed", "Couldn't restart as administrator: {0}"), error), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
                // Declined: UAC said no - stay as we are.
            }
        }
    }
}
