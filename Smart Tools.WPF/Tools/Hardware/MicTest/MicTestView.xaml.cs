using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Microphone test - UI only. Capture, the level numbers, and hearing the input are in
    /// <see cref="MicTestManager"/>; the verdict and settings are in MicTestManager.Logic.cs.
    /// </summary>
    public partial class MicTestView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        /// <summary>If nothing louder than this (dB) is heard for this long, suggest checking Windows' privacy setting.</summary>
        private static readonly TimeSpan SilentHintAfter = TimeSpan.FromSeconds(4);

        private readonly MicTestManager _manager = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly bool _viewConstructed;

        private MicTestSettings _settings = new();
        private double _shownMeter;
        private double _peakMeter;
        private bool _loadedOnce;
        private bool _applying;
        private bool _optionsCollapsed;

        public MicTestView()
        {
            InitializeComponent();
            _manager.Stopped += Manager_Stopped;
            _timer.Tick += Timer_Tick;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops the test and lets go of the microphone.</summary>
        public void CancelActiveRun()
        {
            _timer.Stop();
            SaveSettings();
            _manager.Dispose();
        }

        private string Str(string key) => TryFindResource(key) as string ?? key;

        // =================================================================
        //  Lifetime
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _settings = MicTestManager.LoadSettings();
            _applying = true;
            ChkHear.IsChecked = _settings.HearYourself;
            _applying = false;
            LoadDevices();
        }

        /// <summary>Leaving the page ends the test, so the microphone is never left open behind your back.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_manager.IsRunning) _manager.Stop();
            _timer.Stop();
        }

        // =================================================================
        //  Sidebar
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed
                ? System.Windows.HorizontalAlignment.Center
                : System.Windows.HorizontalAlignment.Left;
            if (BtnCollapseOptions.Content is StackPanel header)
                header.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.Pop(IconRefresh, 0.6);
            LoadDevices();
        }

        private void LoadDevices()
        {
            IReadOnlyList<MicRecorderDevice> devices;
            try
            {
                devices = MicRecorderManager.Devices();
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
            {
                devices = Array.Empty<MicRecorderDevice>();
            }

            _applying = true;
            try
            {
                DeviceCombo.ItemsSource = devices;
                DeviceCombo.SelectedItem = MicRecorderManager.PreferredDevice(devices, _settings.DeviceName);
            }
            finally
            {
                _applying = false;
            }

            bool any = devices.Count > 0;
            TxtDeviceHint.Text = any ? "" : Str("Str_MicTest_NoDevice");
            TxtDeviceHint.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
            BtnStart.IsEnabled = any || _manager.IsRunning;
        }

        private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            SaveSettings();
            // A different microphone mid-test: restart on the new one.
            if (_manager.IsRunning && DeviceCombo.SelectedItem is MicRecorderDevice)
            {
                _manager.Stop();
                _restartAfterStop = true;
            }
        }

        private bool _restartAfterStop;

        private void ChkHear_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            if (_manager.IsRunning) _manager.SetHear(ChkHear.IsChecked == true);
            SaveSettings();
        }

        private void SaveSettings()
        {
            if (!_loadedOnce) return;
            _settings.HearYourself = ChkHear.IsChecked == true;
            if (DeviceCombo.SelectedItem is MicRecorderDevice device) _settings.DeviceName = device.Name;
            MicTestManager.SaveSettings(_settings);
        }

        // =================================================================
        //  The test
        // =================================================================

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.IsRunning)
            {
                _manager.Stop();
                return;
            }
            StartTest();
        }

        private void StartTest()
        {
            if (DeviceCombo.SelectedItem is not MicRecorderDevice device) return;
            try
            {
                _manager.Start(device, ChkHear.IsChecked == true);
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
            {
                ShowVerdictText(string.Format(CultureInfo.CurrentCulture, Str("Str_MicTest_OpenFailed"), ex.Message), error: true);
                PanelPrivacy.Visibility = Visibility.Visible;
                return;
            }
            _shownMeter = 0;
            _peakMeter = 0;
            PanelPrivacy.Visibility = Visibility.Collapsed;
            BtnStart.Content = Str("Str_MicTest_Stop");
            BtnResetPeak.IsEnabled = true;
            LiveDot.Opacity = 1;
            ShowVerdictText(Str("Str_MicTest_V_Waiting"), error: false);
            _timer.Start();
        }

        private void Manager_Stopped(string? error)
        {
            _timer.Stop();
            LiveDot.Opacity = 0.25;
            BtnStart.Content = Str("Str_MicTest_Start");
            BtnResetPeak.IsEnabled = false;
            SetMeter(0, 0);
            TxtDb.Text = "-∞ dB";
            if (_restartAfterStop)
            {
                _restartAfterStop = false;
                StartTest();
                return;
            }
            if (error != null)
                ShowVerdictText(string.Format(CultureInfo.CurrentCulture, Str("Str_MicTest_Stopped"), error), error: true);
            else
                ShowVerdictText(Str("Str_MicTest_Ready"), error: false);
        }

        private void BtnResetPeak_Click(object sender, RoutedEventArgs e)
        {
            _manager.ResetPeak();
            _peakMeter = 0;
            PanelPrivacy.Visibility = Visibility.Collapsed;
        }

        private void BtnOpenPrivacy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No Settings app to open; the hint text already says where to look.
            }
        }

        // =================================================================
        //  Drawing
        // =================================================================

        private void Timer_Tick(object? sender, EventArgs e)
        {
            var s = _manager.Snapshot();
            double target = MicRecorderManager.MeterPercent(s.CurrentPercent);
            // Rise at once, fall smoothly.
            _shownMeter = target >= _shownMeter ? target : Math.Max(target, _shownMeter - 4);
            double peakMeter = MicRecorderManager.MeterPercent(s.PeakPercent);
            _peakMeter = peakMeter;
            SetMeter(_shownMeter, _peakMeter);

            double db = MicTestManager.ToDb(s.CurrentPercent);
            TxtDb.Text = db <= MicTestManager.FloorDb ? "-∞ dB" : string.Format(CultureInfo.CurrentCulture, "{0:0} dB", db);
            TxtPeak.Text = s.PeakPercent <= 0 ? "—" : string.Format(CultureInfo.CurrentCulture, "{0:0} dB", MicTestManager.ToDb(s.PeakPercent));
            TxtQuietest.Text = s.QuietestPercent <= 0 ? "—" : string.Format(CultureInfo.CurrentCulture, "{0:0} dB", MicTestManager.ToDb(s.QuietestPercent));
            TxtClipped.Text = s.ClippedBuffers.ToString(CultureInfo.CurrentCulture);
            TxtTime.Text = $"{(int)s.Elapsed.TotalMinutes}:{s.Elapsed.Seconds:00}";

            var verdict = MicTestManager.Judge(s);
            ShowVerdictText(Str("Str_MicTest_V_" + verdict), error: verdict is MicTestVerdict.Silent or MicTestVerdict.Clipping or MicTestVerdict.TooLoud);
            if (verdict == MicTestVerdict.Silent && s.Elapsed >= SilentHintAfter) PanelPrivacy.Visibility = Visibility.Visible;
            else if (verdict != MicTestVerdict.Silent && verdict != MicTestVerdict.Waiting) PanelPrivacy.Visibility = Visibility.Collapsed;
        }

        private void SetMeter(double percent, double peakPercent)
        {
            LevelScale.ScaleX = Math.Clamp(percent / 100, 0, 1);
            string brush = percent >= 95 ? "StatusErrorFg" : percent >= 80 ? "StatusWarningFg" : "StatusSuccessFg";
            LevelFill.SetResourceReference(Border.BackgroundProperty, brush);
            PlacePeakMarker(peakPercent);
        }

        private void PlacePeakMarker(double peakPercent)
        {
            if (peakPercent <= 0 || MeterTrack.ActualWidth <= 0)
            {
                PeakMarker.Visibility = Visibility.Collapsed;
                return;
            }
            double x = Math.Clamp(peakPercent / 100 * MeterTrack.ActualWidth - PeakMarker.Width, 0, MeterTrack.ActualWidth);
            PeakMarker.Margin = new Thickness(x, 0, 0, 0);
            PeakMarker.Visibility = Visibility.Visible;
        }

        private void MeterTrack_SizeChanged(object sender, SizeChangedEventArgs e) => PlacePeakMarker(_peakMeter);

        private void ShowVerdictText(string text, bool error)
        {
            if (TxtVerdict.Text != text) TxtVerdict.Text = text;
            TxtVerdict.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "TextSecondary");
        }
    }
}
