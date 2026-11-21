using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Application = System.Windows.Application;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Drive Health view - UI only. Listing disks, reading SMART (ATA and
    /// NVMe) and the health verdict live in <see cref="DriveHealthManager"/>;
    /// data shapes in DriveHealthModel.cs. Each drive is a card bound
    /// straight to <see cref="DriveHealthInfo"/> (status colours are
    /// DataTriggers in the XAML).
    /// </summary>
    public partial class DriveHealthView : UserControl
    {
        private static string DialogTitle => Loc.T("Str_Tool_drive-health_Name", "Drive Health Monitor");
        private const double TwoColumnWidth = 1060;
        private const double ScrollBarAllowance = 18;

        /// <summary>Width of one drive card (one or two per row, set from the available width).</summary>
        public static readonly DependencyProperty CardWidthProperty =
            DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(DriveHealthView), new PropertyMetadata(520.0));

        public double CardWidth
        {
            get => (double)GetValue(CardWidthProperty);
            set => SetValue(CardWidthProperty, value);
        }

        private readonly bool _isElevated;
        private CancellationTokenSource? _cts;
        private bool _loadedOnce;
        private bool _bannerDismissed;
        private bool _hasResults;
        private bool _forceRecord;

        public DriveHealthView()
        {
            InitializeComponent();

            // Elevation can't change without a restart, so once is enough.
            _isElevated = !DesignerProperties.GetIsInDesignMode(this) && DriveHealthManager.IsRunningAsAdministrator();
            ShowAdminState();
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun() => _cts?.Cancel();

        // Only the first time the view is shown - Refresh is there for later.
        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await LoadDrivesAsync();
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            // Drives may have been plugged in or removed - scan tools should see that too.
            DriveHealthManager.InvalidateCache();
            await LoadDrivesAsync();
        }

        // =================================================================
        //  Drives / History
        // =================================================================

        private void View_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool history = RadioHistory.IsChecked == true;
            DrivesArea.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
            History.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
            if (history) UiMotion.FadeIn(History, 8);
        }

        /// <summary>Record now in the History view: read the drives again and add a reading even if the last one is recent.</summary>
        private async void History_RecordNowRequested(object? sender, EventArgs e)
        {
            _forceRecord = true;
            DriveHealthManager.InvalidateCache();
            await LoadDrivesAsync();
        }

        private void DriveScroller_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double available = Math.Max(0, e.NewSize.Width - ScrollBarAllowance);
            int columns = available >= TwoColumnWidth ? 2 : 1;
            CardWidth = Math.Max(280, Math.Floor(available / columns) - 14);
        }

        // =================================================================
        //  Load
        // =================================================================

        private async Task LoadDrivesAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            SetLoading(true);
            try
            {
                var scan = await DriveHealthManager.GetAllDrivesAsync(token);
                if (token.IsCancellationRequested) return;
                ShowScan(scan);
            }
            catch (OperationCanceledException)
            {
                // closing or superseded
            }
            catch (Exception ex)
            {
                // WMI can fail outright in locked-down environments.
                ShowEmpty($"Couldn't read drive information: {ex.Message}");
                TxtStatus.Text = "Couldn't read drive information.";
            }
            finally
            {
                if (!token.IsCancellationRequested) SetLoading(false);
            }
        }

        private void SetLoading(bool loading)
        {
            BtnRefresh.IsEnabled = !loading;
            LoadingIndicator.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            SpinRefreshIcon(loading);

            if (loading)
            {
                TxtStatus.Text = "Reading drive information...";
                // Keep the current cards while refreshing; only an empty view shows the placeholder.
                if (!_hasResults) ShowEmpty("Reading drive information...");
            }
        }

        private void ShowScan(DriveHealthScan scan)
        {
            if (scan.ErrorMessage != null)
            {
                DriveList.ItemsSource = null;
                _hasResults = false;
                SummaryPanel.Visibility = Visibility.Collapsed;
                ShowEmpty(scan.ErrorMessage);
                TxtStatus.Text = "Couldn't read drive information.";
                return;
            }

            DriveList.ItemsSource = scan.Drives;
            _hasResults = scan.Drives.Count > 0;

            // Every scan also adds a SMART reading to the history (at most one per 6 hours unless asked).
            History.ApplyScan(scan, _forceRecord);
            _forceRecord = false;

            if (!_hasResults)
            {
                ShowEmpty(TryFindResource("Str_DriveHealth_EmptyState") as string ?? "No drives found.");
            }
            else
            {
                UiMotion.FadeVisibility(EmptyStatePanel, false);
                UiMotion.FadeIn(DriveList, 8);
            }

            // Summary pills
            TxtDriveCount.Text = scan.Drives.Count == 1 ? "1 drive" : $"{scan.Drives.Count} drives";
            SetPill(HealthyPill, TxtHealthyCount, scan.HealthyCount, "healthy");
            SetPill(WarningPill, TxtWarningCount, scan.WarningCount, scan.WarningCount == 1 ? "warning" : "warnings");
            SetPill(UnhealthyPill, TxtUnhealthyCount, scan.UnhealthyCount, "failing");
            bool firstShow = SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = Visibility.Visible;
            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);

            int withSmart = scan.Drives.Count(d => d.SmartAvailable);
            TxtStatus.Text = $"Checked at {DateTime.Now:t} · SMART details from {withSmart} of {scan.Drives.Count} drive(s) · {scan.Elapsed.TotalSeconds:0.0} s";

            // Only nag about admin when it would actually add data.
            bool adminWouldHelp = !_isElevated && scan.Drives.Any(d => d.UnavailableReason == SmartUnavailableReason.NeedsAdministrator);
            UiMotion.FadeVisibility(ElevationBanner, adminWouldHelp && !_bannerDismissed);

            if (scan.UnhealthyCount > 0)
            {
                try
                {
                    var failing = scan.Drives.First(d => d.HealthStatus == DriveHealthStatus.Unhealthy);
                    ToastNotificationManager.ShowToast("Drive problem found",
                        $"{failing.FriendlyName} ({failing.DriveLettersDisplay}) may be failing. Back up its data.");
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }
            }
        }

        private static void SetPill(FrameworkElement pill, System.Windows.Controls.TextBlock text, int count, string label)
        {
            pill.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            text.Text = $"{count} {label}";
        }

        private void ShowEmpty(string message)
        {
            TxtEmptyState.Text = message;
            UiMotion.FadeVisibility(EmptyStatePanel, true);
        }

        private void ShowAdminState()
        {
            if (_isElevated)
            {
                AdminPill.SetResourceReference(BackgroundProperty, "StatusSuccessBg");
                IconAdmin.SetResourceReference(ForegroundProperty, "StatusSuccessFg");
                TxtAdmin.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusSuccessFg");
                IconAdmin.Kind = PackIconKind.ShieldAccount;
                TxtAdmin.Text = "Administrator";
                AdminPill.ToolTip = "Running as administrator - full SMART details are available.";
            }
            else
            {
                AdminPill.ToolTip = "Running without administrator rights. NVMe details usually still work; SATA SMART needs admin.";
            }
        }

        /// <summary>Spins the Refresh icon while loading.</summary>
        private void SpinRefreshIcon(bool spin)
        {
            if (spin)
            {
                var rotate = new RotateTransform();
                IconRefresh.RenderTransformOrigin = new Point(0.5, 0.5);
                IconRefresh.RenderTransform = rotate;
                rotate.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else if (IconRefresh.RenderTransform is RotateTransform rotate)
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                rotate.Angle = 0;
            }
        }

        // =================================================================
        //  Elevation
        // =================================================================

        /// <summary>
        /// Relaunches Smart Tools elevated (UAC prompt) and closes this
        /// instance. Asks first - the old version closed immediately, losing
        /// anything unsaved in other tools.
        /// </summary>
        private void BtnRestartAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            var answer = AppModal.Show(Loc.T("Str_MB_SmartToolsWillClose", "Smart Tools will close and reopen as administrator so it can read SMART details.\n\nAnything unsaved in other tools will be lost. Continue?"),
                DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    AppModal.Show(Loc.T("Str_MB_CouldnTFindThis", "Couldn't find this app's executable to restart it."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, Verb = "runas" });
                Application.Current.Shutdown();
            }
            catch (Win32Exception)
            {
                // UAC declined or blocked by policy - just stay as we are.
            }
            catch (Exception ex)
            {
                AppModal.Show(Loc.F("Str_MB_CouldnTRestartAs", "Couldn't restart as administrator: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDismissBanner_Click(object sender, RoutedEventArgs e)
        {
            _bannerDismissed = true;
            UiMotion.FadeVisibility(ElevationBanner, false);
        }
    }
}
