using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Webcam test - UI only. Listing cameras and sizes and running the preview are in <see cref="WebcamTestManager"/>
    /// (the bundled FFmpeg); list parsing, brightness and settings are in WebcamTestManager.Logic.cs.
    /// </summary>
    public partial class WebcamTestView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        /// <summary>No picture after this long means the camera is on but blocked or busy.</summary>
        private static readonly TimeSpan NoPictureAfter = TimeSpan.FromSeconds(5);

        private readonly WebcamTestManager _manager = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly bool _viewConstructed;

        private WebcamTestSettings _settings = new();
        private WriteableBitmap? _bitmap;
        private byte[] _frame = Array.Empty<byte>();
        private long _lastFrame;
        private DateTime _startedAt;
        private int _listVersion;
        private bool _loadedOnce;
        private bool _applying;
        private bool _optionsCollapsed;

        public WebcamTestView()
        {
            InitializeComponent();
            _manager.Stopped += Manager_Stopped;
            _timer.Tick += Timer_Tick;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops the preview so the camera light goes off.</summary>
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

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _settings = WebcamTestManager.LoadSettings();
            _applying = true;
            ChkMirror.IsChecked = _settings.Mirror;
            MirrorTransform.ScaleX = _settings.Mirror ? -1 : 1;
            _applying = false;
            await LoadDevicesAsync();
        }

        /// <summary>Leaving the page turns the camera off, so it is never left on behind your back.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_manager.IsRunning) StopTest();
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

        private async void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.Pop(IconRefresh, 0.6);
            if (_manager.IsRunning) StopTest();
            await LoadDevicesAsync();
        }

        private async Task LoadDevicesAsync()
        {
            if (!FfmpegLocator.IsAvailable)
            {
                ShowHint(Str("Str_WebcamTest_NoFfmpeg"), error: true);
                BtnStart.IsEnabled = false;
                return;
            }

            int version = ++_listVersion;
            ShowHint(Str("Str_WebcamTest_Looking"), error: false);
            var devices = await WebcamTestManager.ListDevicesAsync();
            if (version != _listVersion) return;   // a newer look is under way

            _applying = true;
            try
            {
                DeviceCombo.ItemsSource = devices;
                DeviceCombo.SelectedItem = WebcamTestManager.PreferredDevice(devices, _settings.DeviceName);
            }
            finally
            {
                _applying = false;
            }

            ShowHint(devices.Count == 0 ? Str("Str_WebcamTest_NoDevice") : "", error: true);
            BtnStart.IsEnabled = devices.Count > 0;
            await LoadModesAsync();
        }

        private async Task LoadModesAsync()
        {
            if (DeviceCombo.SelectedItem is not WebcamDevice device)
            {
                ModeCombo.ItemsSource = null;
                return;
            }

            int version = ++_listVersion;
            var modes = await WebcamTestManager.ListModesAsync(device);
            if (version != _listVersion) return;
            if (modes.Count == 0) modes.Add(WebcamTestManager.DefaultMode);

            _applying = true;
            try
            {
                ModeCombo.ItemsSource = modes;
                ModeCombo.SelectedItem = WebcamTestManager.PreferredMode(modes, _settings.ModeKey);
            }
            finally
            {
                _applying = false;
            }
        }

        private async void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            SaveSettings();
            bool wasRunning = _manager.IsRunning;
            if (wasRunning) StopTest();
            await LoadModesAsync();
            if (wasRunning) StartTest();
        }

        private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            SaveSettings();
            if (_manager.IsRunning)
            {
                StopTest();
                StartTest();
            }
        }

        private void ChkMirror_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            MirrorTransform.ScaleX = ChkMirror.IsChecked == true ? -1 : 1;
            SaveSettings();
        }

        private void SaveSettings()
        {
            if (!_loadedOnce) return;
            _settings.Mirror = ChkMirror.IsChecked == true;
            if (DeviceCombo.SelectedItem is WebcamDevice device) _settings.DeviceName = device.Name;
            if (ModeCombo.SelectedItem is WebcamMode mode) _settings.ModeKey = mode.Key;
            WebcamTestManager.SaveSettings(_settings);
        }

        private void ShowHint(string text, bool error)
        {
            TxtDeviceHint.Text = text;
            TxtDeviceHint.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "SidebarTextMuted");
        }

        // =================================================================
        //  The preview
        // =================================================================

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.IsRunning) StopTest();
            else StartTest();
        }

        private void StartTest()
        {
            if (DeviceCombo.SelectedItem is not WebcamDevice device) return;
            var mode = ModeCombo.SelectedItem as WebcamMode ?? WebcamTestManager.DefaultMode;
            try
            {
                _manager.Start(device, mode);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                ShowVerdict(string.Format(CultureInfo.CurrentCulture, Str("Str_WebcamTest_StartFailed"), ex.Message), error: true);
                return;
            }

            _bitmap = new WriteableBitmap(_manager.Width, _manager.Height, 96, 96, PixelFormats.Bgr24, null);
            _frame = new byte[_manager.Width * _manager.Height * 3];
            _lastFrame = 0;
            _startedAt = DateTime.Now;
            Preview.Source = _bitmap;
            EmptyPanel.Visibility = Visibility.Collapsed;
            PanelPrivacy.Visibility = Visibility.Collapsed;
            LiveDot.Opacity = 1;
            BtnStart.Content = Str("Str_WebcamTest_Stop");
            TxtResolution.Text = $"{_manager.Width} × {_manager.Height}";
            ShowVerdict(Str("Str_WebcamTest_Starting"), error: false);
            _timer.Start();
        }

        private void StopTest()
        {
            _timer.Stop();
            _manager.Stop();
            ResetUi();
            ShowVerdict(Str("Str_WebcamTest_Ready"), error: false);
        }

        private void ResetUi()
        {
            Preview.Source = null;
            _bitmap = null;
            EmptyPanel.Visibility = Visibility.Visible;
            LiveDot.Opacity = 0.25;
            BtnStart.Content = Str("Str_WebcamTest_Start");
            BtnSnapshot.IsEnabled = false;
            TxtResolution.Text = TxtFps.Text = TxtBrightness.Text = "—";
        }

        private void Manager_Stopped(string reason)
        {
            _timer.Stop();
            ResetUi();
            ShowVerdict(string.Format(CultureInfo.CurrentCulture, Str("Str_WebcamTest_Stopped"), reason), error: true);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_bitmap == null) return;
            if (_manager.TryGetFrame(_frame, ref _lastFrame))
                _bitmap.WritePixels(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight), _frame, _bitmap.PixelWidth * 3, 0);

            var stats = _manager.Stats();
            if (stats.Frames == 0)
            {
                if (DateTime.Now - _startedAt >= NoPictureAfter)
                {
                    ShowVerdict(Str("Str_WebcamTest_V_NoPicture"), error: true);
                    PanelPrivacy.Visibility = Visibility.Visible;
                }
                return;
            }

            PanelPrivacy.Visibility = Visibility.Collapsed;
            BtnSnapshot.IsEnabled = true;
            TxtFps.Text = stats.Fps <= 0 ? "—" : stats.Fps.ToString("0", CultureInfo.CurrentCulture);
            TxtBrightness.Text = string.Format(CultureInfo.CurrentCulture, "{0:0}%", stats.BrightnessPercent);
            var brightness = WebcamTestManager.JudgeBrightness(stats.BrightnessPercent);
            ShowVerdict(Str("Str_WebcamTest_V_" + brightness), error: brightness != WebcamBrightness.Good);
        }

        private void ShowVerdict(string text, bool error)
        {
            if (TxtVerdict.Text != text) TxtVerdict.Text = text;
            TxtVerdict.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "TextSecondary");
        }

        private void BtnOpenPrivacy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No Settings app to open; the hint text already says where to look.
            }
        }

        // =================================================================
        //  Snapshot
        // =================================================================

        private void BtnSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (_bitmap == null) return;
            var dialog = new SaveFileDialog
            {
                Title = Str("Str_WebcamTest_Snapshot"),
                Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg",
                FileName = $"Webcam {DateTime.Now:yyyy-MM-dd HH-mm-ss}",
                AddExtension = true,
                DefaultExt = ".png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog() != true) return;

            BitmapEncoder encoder = Path.GetExtension(dialog.FileName).ToLowerInvariant() is ".jpg" or ".jpeg"
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            try
            {
                // The picture as the camera sees it, not mirrored.
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(
                    _bitmap.PixelWidth, _bitmap.PixelHeight, 96, 96, PixelFormats.Bgr24, null, _frame, _bitmap.PixelWidth * 3)));
                using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write);
                encoder.Save(stream);
                ShowVerdict(string.Format(CultureInfo.CurrentCulture, Str("Str_WebcamTest_SnapshotSaved"), Path.GetFileName(dialog.FileName)), error: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowVerdict(string.Format(CultureInfo.CurrentCulture, Str("Str_WebcamTest_SnapshotFailed"), ex.Message), error: true);
            }
        }
    }
}
