using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Services - UI only. Reading and changing services is in <see cref="ServicesManager"/>.
    /// Slow changes (a service can take seconds to stop) run on a background thread while the row is marked busy.
    /// </summary>
    public partial class ServicesView : UserControl
    {
        private const string DialogTitle = "Services";

        private enum ServiceFilter { All, Running, Stopped }

        private readonly ServicesManager _services = new();
        private readonly ObservableCollection<WinService> _shown = new();
        private List<WinService> _all = new();
        private ServiceFilter _filter = ServiceFilter.All;
        private readonly bool _viewConstructed;
        private bool _loadedOnce, _scanning;

        public ServicesView()
        {
            InitializeComponent();
            GridServices.ItemsSource = _shown;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing. Nothing is held open between changes.</summary>
        public void CancelActiveRun() { }

        internal static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        internal static string StateText(WinServiceState state) => state switch
        {
            WinServiceState.Running => T("Str_Services_StateRunning", "Running"),
            WinServiceState.Stopped => T("Str_Services_StateStopped", "Stopped"),
            WinServiceState.Paused => T("Str_Services_StatePaused", "Paused"),
            WinServiceState.StartPending => T("Str_Services_StateStarting", "Starting"),
            WinServiceState.StopPending => T("Str_Services_StateStopping", "Stopping"),
            WinServiceState.PausePending => T("Str_Services_StatePausing", "Pausing"),
            WinServiceState.ContinuePending => T("Str_Services_StateResuming", "Resuming"),
            _ => T("Str_Services_StateUnknown", "Unknown"),
        };

        internal static string ModeText(WinServiceStartMode mode) => mode switch
        {
            WinServiceStartMode.Automatic => T("Str_Services_ModeAutomatic", "Automatic"),
            WinServiceStartMode.AutomaticDelayed => T("Str_Services_ModeDelayed", "Automatic (delayed)"),
            WinServiceStartMode.Manual => T("Str_Services_ModeManual", "Manual"),
            _ => T("Str_Services_ModeDisabled", "Disabled"),
        };

        private async void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ElevationBanner.ShowIfNotElevated();
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
                _all = await Task.Run(_services.Scan);
                Refresh();
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
            {
                SetStatus(T("Str_Services_ReadFailed", "Couldn't read the list of services."), "StatusErrorFg");
            }
            finally
            {
                BtnRefresh.IsEnabled = true;
                _scanning = false;
            }
        }

        // =================================================================
        //  List
        // =================================================================

        private void Refresh()
        {
            _shown.Clear();
            string query = TxtSearch.Text.Trim();
            foreach (var service in _all)
            {
                if (_filter == ServiceFilter.Running && service.State == WinServiceState.Stopped) continue;
                if (_filter == ServiceFilter.Stopped && service.State != WinServiceState.Stopped) continue;
                if (query.Length > 0 &&
                    !service.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                    !service.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                    !service.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
                _shown.Add(service);
            }

            UpdateCount();
            PanelEmpty.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool any = _all.Count > 0;
            TxtEmptyTitle.Text = any ? T("Str_Services_NoMatches", "Nothing matches") : T("Str_Services_EmptyTitle", "No services found");
            TxtEmptyHint.Text = any ? T("Str_Services_NoMatchesHint", "Try another search or filter.") : T("Str_Services_EmptyHint", "Windows didn't list any services. Try scanning again.");
        }

        private void UpdateCount()
        {
            int running = _all.Count(s => s.State != WinServiceState.Stopped);
            TxtCount.Text = string.Format(T("Str_Services_Count", "{0} services, {1} running"), _all.Count, running);
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed) Refresh();
        }

        private void RadioFilter_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int index)) return;
            _filter = (ServiceFilter)index;
            Refresh();
        }

        // =================================================================
        //  Start, stop, restart
        // =================================================================

        private static WinService? ServiceOf(object sender) => (sender as FrameworkElement)?.DataContext as WinService;

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (ServiceOf(sender) is not { } service) return;
            await RunAsync(service,
                string.Format(T("Str_Services_Starting", "Starting {0}..."), service.DisplayName),
                string.Format(T("Str_Services_Started", "{0} started."), service.DisplayName),
                () => _services.Start(service));
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (ServiceOf(sender) is not { } service) return;
            var dependents = _services.RunningDependents(service);
            if (!ConfirmRisk(service, dependents, "Str_Services_DependentsConfirm",
                    "{0} has running services that depend on it: {1}. They will be stopped too. Continue?")) return;

            await RunAsync(service,
                string.Format(T("Str_Services_Stopping", "Stopping {0}..."), service.DisplayName),
                string.Format(T("Str_Services_StoppedDone", "{0} stopped."), service.DisplayName),
                () => _services.Stop(service, dependents.Count > 0));
        }

        private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            if (ServiceOf(sender) is not { } service) return;
            var dependents = _services.RunningDependents(service);
            if (!ConfirmRisk(service, dependents, "Str_Services_RestartDependentsConfirm",
                    "{0} has running services that depend on it: {1}. They will be stopped and started again with it. Continue?")) return;

            await RunAsync(service,
                string.Format(T("Str_Services_Restarting", "Restarting {0}..."), service.DisplayName),
                string.Format(T("Str_Services_Restarted", "{0} restarted."), service.DisplayName),
                () => _services.Restart(service));
        }

        /// <summary>Asks before stopping a core Windows service, and before taking down services that depend on this one.</summary>
        private bool ConfirmRisk(WinService service, List<WinService> dependents, string dependentsKey, string dependentsFallback)
        {
            if (service.IsCore && !Ask(string.Format(T("Str_Services_CoreConfirm",
                    "{0} is a core Windows service. Stopping or disabling it can break networking, security, or other parts of Windows. Continue?"), service.DisplayName))) return false;
            if (dependents.Count > 0 && !Ask(string.Format(T(dependentsKey, dependentsFallback), service.DisplayName, JoinNames(dependents)))) return false;
            return true;
        }

        private static bool Ask(string question) =>
            MessageBox.Show(question, DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        /// <summary>"A", "A and B", "A, B, and C", or "A, B, C, and 4 more" - Oxford comma in English.</summary>
        private static string JoinNames(List<WinService> services)
        {
            var names = services.Select(s => s.DisplayName).ToList();
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return string.Format(T("Str_Services_ListTwo", "{0} and {1}"), names[0], names[1]);
            string last = names.Count <= 3 ? names[^1] : string.Format(T("Str_Services_ListMore", "{0} more"), names.Count - 3);
            string head = string.Join(", ", names.Take(names.Count <= 3 ? names.Count - 1 : 3));
            return string.Format(T("Str_Services_ListMany", "{0}, and {1}"), head, last);
        }

        // =================================================================
        //  More menu: pause, resume, startup type, copy name
        // =================================================================

        private void BtnMore_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: WinService service } button) return;

            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };

            if (service.AcceptsPause && service.IsRunning)
                menu.Items.Add(MenuEntry(T("Str_Services_Pause", "Pause"), async () => await RunAsync(service,
                    string.Format(T("Str_Services_Pausing", "Pausing {0}..."), service.DisplayName),
                    string.Format(T("Str_Services_PausedDone", "{0} paused."), service.DisplayName),
                    () => _services.Pause(service))));
            if (service.IsPaused)
                menu.Items.Add(MenuEntry(T("Str_Services_Resume", "Resume"), async () => await RunAsync(service,
                    string.Format(T("Str_Services_Resuming", "Resuming {0}..."), service.DisplayName),
                    string.Format(T("Str_Services_ResumedDone", "{0} resumed."), service.DisplayName),
                    () => _services.Resume(service))));

            var startup = new MenuItem { Header = T("Str_Services_StartupType", "Startup type") };
            foreach (var mode in new[] { WinServiceStartMode.Automatic, WinServiceStartMode.AutomaticDelayed, WinServiceStartMode.Manual, WinServiceStartMode.Disabled })
            {
                var item = MenuEntry(ModeText(mode), async () => await ChangeStartModeAsync(service, mode));
                item.IsChecked = service.StartMode == mode;
                startup.Items.Add(item);
            }
            menu.Items.Add(startup);

            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry(T("Str_Services_CopyName", "Copy service name"), () => CopyName(service)));
            menu.IsOpen = true;
        }

        private static MenuItem MenuEntry(string header, Action click)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => click();
            return item;
        }

        private async Task ChangeStartModeAsync(WinService service, WinServiceStartMode mode)
        {
            if (service.StartMode == mode) return;
            if (mode == WinServiceStartMode.Disabled && service.IsCore &&
                !Ask(string.Format(T("Str_Services_CoreConfirm",
                    "{0} is a core Windows service. Stopping or disabling it can break networking, security, or other parts of Windows. Continue?"), service.DisplayName))) return;

            await RunAsync(service,
                string.Format(T("Str_Services_Changing", "Changing {0}..."), service.DisplayName),
                string.Format(T("Str_Services_StartupChanged", "Startup type for {0} is now {1}."), service.DisplayName, ModeText(mode)),
                () => _services.SetStartMode(service, mode));
        }

        private void CopyName(WinService service)
        {
            try
            {
                Clipboard.SetText(service.Name);
            }
            catch (COMException)
            {
                // The clipboard is busy; nothing was copied and nothing is lost.
            }
        }

        // =================================================================
        //  Running a change
        // =================================================================

        /// <summary>Marks the row busy, runs the change off the UI thread, then reports how it went.</summary>
        private async Task RunAsync(WinService service, string busyText, string doneText, Func<WinServiceChangeResult> change)
        {
            service.IsBusy = true;
            SetStatus(busyText, "TextSecondary");
            var result = WinServiceChangeResult.Failed;
            try
            {
                result = await Task.Run(change);
            }
            catch (Exception ex) when (ex is ManagementException or COMException or Win32Exception or InvalidOperationException)
            {
                // Reported below as a failed change.
            }
            finally
            {
                service.IsBusy = false;
            }
            UpdateCount();
            Report(result, doneText);
        }

        private void Report(WinServiceChangeResult result, string doneText)
        {
            switch (result)
            {
                case WinServiceChangeResult.Done:
                    SetStatus(doneText, "StatusSuccessFg");
                    break;
                case WinServiceChangeResult.NeedsAdministrator:
                    ElevationBanner.ShowIfNotElevated();
                    SetStatus(T("Str_Services_NeedsAdmin", "Windows blocked this change. You may need administrator rights, or the service may be protected."), "StatusErrorFg");
                    break;
                case WinServiceChangeResult.HasRunningDependents:
                    SetStatus(T("Str_Services_HasDependents", "Other running services depend on this one. Stop them first."), "StatusErrorFg");
                    break;
                case WinServiceChangeResult.Disabled:
                    SetStatus(T("Str_Services_IsDisabled", "This service is disabled. Change its startup type to start it."), "StatusErrorFg");
                    break;
                case WinServiceChangeResult.TimedOut:
                    SetStatus(T("Str_Services_TimedOut", "The service didn't respond in time. Check its status and try again."), "StatusErrorFg");
                    break;
                default:
                    SetStatus(T("Str_Services_Failed", "Couldn't change that service. It may be in use or protected."), "StatusErrorFg");
                    break;
            }
        }

        private void SetStatus(string? text, string colorKey)
        {
            TxtStatus.Text = text ?? "";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            TxtStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Shows a service's state as text in the current language.</summary>
    internal sealed class ServiceStateTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is WinServiceState state ? ServicesView.StateText(state) : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Shows a service's startup type as text in the current language.</summary>
    internal sealed class ServiceStartModeTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is WinServiceStartMode mode ? ServicesView.ModeText(mode) : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
