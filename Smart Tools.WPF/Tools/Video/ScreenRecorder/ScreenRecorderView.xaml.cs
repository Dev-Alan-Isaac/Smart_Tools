using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Screen Recorder - UI only. Running FFmpeg, finishing the file, and listing screens, windows, and microphones are in
    /// <see cref="ScreenRecorderManager"/>. While recording, a small bar (<see cref="ScreenRecorderControlWindow"/>) shows the time
    /// and a Stop button, so the app can be minimized out of the picture.
    /// </summary>
    public partial class ScreenRecorderView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;
        private const string DialogTitle = "Screen Recorder";

        private readonly ScreenRecorderManager _recorder = new();
        private readonly ObservableCollection<RecordingEntry> _recordings = new();
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private ScreenRecorderControlWindow? _bar;
        private ScreenRecorderSettings _settings = new();
        private RecordRegion? _area;
        private bool _viewConstructed, _loadedOnce, _applying, _busy, _optionsCollapsed, _picking, _closed;
        private bool _minimizedByUs;
        private RecordTarget _recordingTarget;
        private RecordRegion? _recordingRegion;   // the screen being recorded now (null = all screens)

        public ScreenRecorderView()
        {
            InitializeComponent();
            GridRecordings.ItemsSource = _recordings;
            _recordings.CollectionChanged += (_, _) => PanelEmpty.Visibility = _recordings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _clock.Tick += (_, _) => UpdateTimer();
            _recorder.EndedUnexpectedly += OnEndedUnexpectedly;
            _recorder.PausedChanged += OnPausedChanged;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: finishes a recording in progress (the file stays playable) and closes the bar.</summary>
        public void CancelActiveRun()
        {
            _closed = true;
            _clock.Stop();
            _recorder.Dispose();
            CloseBar();
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private async void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _settings = ScreenRecorderManager.LoadSettings();
            ApplySettings(_settings);

            FillScreens();
            FillWindows();
            await FillMicrophonesAsync();

            if (!ScreenRecorderManager.IsAvailable)
            {
                BtnRecord.IsEnabled = false;
                SetStatus(T("Str_Recorder_NoFfmpeg", "FFmpeg isn't installed with this copy of Smart Tools, so recording isn't available."), "StatusErrorFg");
            }
            else
            {
                ShowReady();
            }
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? OptionsCollapsedWidth : OptionsExpandedWidth);
        }

        // =================================================================
        //  Settings and choices
        // =================================================================

        private RadioButton[] TargetButtons => new[] { TargetScreen, TargetWindow, TargetArea };
        private RadioButton[] FpsButtons => new[] { Fps15, Fps30, Fps60 };
        private RadioButton[] QualityButtons => new[] { QualSmall, QualBalanced, QualHigh };

        private void ApplySettings(ScreenRecorderSettings s)
        {
            _applying = true;
            try
            {
                foreach (var r in TargetButtons) r.IsChecked = (string)r.Tag == s.Target.ToString();
                foreach (var r in FpsButtons) r.IsChecked = (string)r.Tag == s.Fps.ToString(CultureInfo.InvariantCulture);
                foreach (var r in QualityButtons) r.IsChecked = (string)r.Tag == s.Quality.ToString();
                ChkCursor.IsChecked = s.ShowCursor;
                ChkMinimize.IsChecked = s.MinimizeWhileRecording;
                ChkSkipUac.IsChecked = s.SkipSecurityPrompts;
            }
            finally
            {
                _applying = false;
            }
            ShowTargetPanel(s.Target);
        }

        private ScreenRecorderSettings ReadSettings() => new()
        {
            Target = Enum.TryParse(TargetButtons.FirstOrDefault(r => r.IsChecked == true)?.Tag as string, out RecordTarget t) ? t : _settings.Target,
            Fps = int.TryParse(FpsButtons.FirstOrDefault(r => r.IsChecked == true)?.Tag as string, out int fps) ? fps : _settings.Fps,
            Quality = Enum.TryParse(QualityButtons.FirstOrDefault(r => r.IsChecked == true)?.Tag as string, out RecordQuality q) ? q : _settings.Quality,
            ShowCursor = ChkCursor.IsChecked == true,
            MinimizeWhileRecording = ChkMinimize.IsChecked == true,
            SkipSecurityPrompts = ChkSkipUac.IsChecked == true,
        };

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            _settings = ReadSettings();
            ScreenRecorderManager.SaveSettings(_settings);
        }

        private void Target_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            _settings = ReadSettings();
            ScreenRecorderManager.SaveSettings(_settings);
            ShowTargetPanel(_settings.Target);
            if (_settings.Target == RecordTarget.Window) FillWindows();   // the list of open windows changes all the time
        }

        private void ShowTargetPanel(RecordTarget target)
        {
            PanelScreen.Visibility = target == RecordTarget.Screen ? Visibility.Visible : Visibility.Collapsed;
            PanelWindow.Visibility = target == RecordTarget.Window ? Visibility.Visible : Visibility.Collapsed;
            PanelArea.Visibility = target == RecordTarget.Area ? Visibility.Visible : Visibility.Collapsed;
            UpdateAreaText();
        }

        private void Choice_Changed(object sender, SelectionChangedEventArgs e) { }

        private void FillScreens()
        {
            var screens = ScreenRecorderManager.ListScreens();
            CmbScreens.ItemsSource = screens;
            CmbScreens.SelectedItem = screens.FirstOrDefault(s => s.IsPrimary) ?? screens.FirstOrDefault();
        }

        private void FillWindows()
        {
            long? keep = (CmbWindows.SelectedItem as RecordWindowInfo)?.Handle;
            var windows = ScreenRecorderManager.ListWindows();
            CmbWindows.ItemsSource = windows;
            CmbWindows.SelectedItem = windows.FirstOrDefault(w => w.Handle == keep) ?? windows.FirstOrDefault();
        }

        private void BtnRefreshWindows_Click(object sender, RoutedEventArgs e) => FillWindows();

        private async Task FillMicrophonesAsync()
        {
            string? keep = CmbMic.SelectedItem as string;
            var mics = await ScreenRecorderManager.ListMicrophonesAsync();
            var items = new List<string> { T("Str_Recorder_NoSound", "No sound") };
            items.AddRange(mics);
            CmbMic.ItemsSource = items;
            CmbMic.SelectedItem = keep != null && items.Contains(keep) ? keep : items[0];
        }

        private async void BtnRefreshMic_Click(object sender, RoutedEventArgs e) => await FillMicrophonesAsync();

        // =================================================================
        //  Picking an area
        // =================================================================

        private async void BtnPickArea_Click(object sender, RoutedEventArgs e) => await PickAreaAsync();

        /// <summary>Hides the app, freezes the screen, and lets the user drag a rectangle (the same overlay the OCR tool uses).</summary>
        private async Task<bool> PickAreaAsync()
        {
            if (_picking) return false;
            _picking = true;
            var main = Window.GetWindow(this);
            Int32Rect? selection = null;
            var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
            try
            {
                main?.Hide();
                await Task.Delay(250);   // let Windows repaint what was behind the app
                var screen = await Task.Run(ScreenOcrManager.CaptureVirtualScreen);
                var dpi = VisualTreeHelper.GetDpi(this);
                var inDips = new Rect(virtualScreen.Left / dpi.DpiScaleX, virtualScreen.Top / dpi.DpiScaleY, virtualScreen.Width / dpi.DpiScaleX, virtualScreen.Height / dpi.DpiScaleY);
                var overlay = new ScreenOcrSelectionWindow(ScreenOcrSelectionWindow.ToBitmapSource(screen), inDips);
                overlay.ShowDialog();
                selection = overlay.Selection;
            }
            catch (Exception ex)
            {
                // Typically the secure desktop (UAC prompt, lock screen): nothing can be captured then.
                SetStatus(T("Str_Recorder_PickFailed", "Couldn't show the screen to choose from: ") + ex.Message, "StatusErrorFg");
            }
            finally
            {
                if (main != null)
                {
                    main.Show();
                    main.Activate();
                }
                _picking = false;
            }

            if (selection is not Int32Rect r) return false;
            var region = ScreenRecorderManager.Normalize(new RecordRegion(virtualScreen.Left + r.X, virtualScreen.Top + r.Y, r.Width, r.Height));
            if (region == null)
            {
                SetStatus(T("Str_Recorder_AreaTooSmall", "That area is too small. Drag a larger one."), "StatusErrorFg");
                return false;
            }
            _area = region;
            UpdateAreaText();
            return true;
        }

        private void UpdateAreaText() =>
            TxtArea.Text = _area == null
                ? T("Str_Recorder_NoArea", "No area chosen yet.")
                : string.Format(T("Str_Recorder_AreaText", "{0} × {1} pixels"), _area.Width, _area.Height);

        // =================================================================
        //  Record and stop
        // =================================================================

        private async void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (_recorder.IsRecording) await StopAsync();
            else await StartAsync();
        }

        private async Task StartAsync()
        {
            _settings = ReadSettings();
            var target = _settings.Target;
            RecordRegion? region = null;
            RecordWindowInfo? window = null;

            switch (target)
            {
                case RecordTarget.Screen:
                    region = (CmbScreens.SelectedItem as RecordScreenInfo)?.Region;   // null = all screens
                    break;
                case RecordTarget.Window:
                    window = CmbWindows.SelectedItem as RecordWindowInfo;
                    if (window == null || !ScreenRecorderManager.IsWindowRecordable(window.Handle))
                    {
                        FillWindows();
                        SetStatus(T("Str_Recorder_PickWindow", "Choose an open window that isn't minimized."), "StatusErrorFg");
                        return;
                    }
                    break;
                default:
                    if (_area == null && !await PickAreaAsync()) return;
                    region = _area;
                    break;
            }

            if (OutputCard.Status.IsError)
            {
                SetStatus(T("Str_Recorder_CantSave", "Can't save there: ") + OutputCard.Status.Text, "StatusErrorFg");
                return;
            }
            string? folder;
            try
            {
                folder = OutputCard.EnsureFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                SetStatus(T("Str_Recorder_CantSave", "Can't save there: ") + ex.Message, "StatusErrorFg");
                return;
            }
            if (string.IsNullOrEmpty(folder)) return;

            long? free = OutputFolderManager.FreeSpace(folder);
            if (free is long left && left < OutputFolderManager.LowSpaceBytes &&
                MessageBox.Show(string.Format(T("Str_Recorder_LowSpace", "There is only {0} free where recordings are saved. Recording could fill the drive. Record anyway?"), OutputFolderManager.FormatBytes(left)), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            string mic = CmbMic.SelectedIndex > 0 ? (CmbMic.SelectedItem as string ?? "") : "";
            var options = new RecordOptions
            {
                Target = target,
                Region = region,
                WindowHandle = window?.Handle ?? 0,
                WindowTitle = window?.Title ?? "",
                Fps = _settings.Fps,
                Quality = _settings.Quality,
                ShowCursor = _settings.ShowCursor,
                Microphone = mic.Length > 0 ? mic : null,
                SkipSecurityPrompts = _settings.SkipSecurityPrompts,
                OutputPath = OutputFolderManager.UniquePath(Path.Combine(folder, ScreenRecorderManager.DefaultFileName(DateTime.Now))),
            };

            SetBusy(true);
            SetStatus(T("Str_Recorder_Starting", "Starting…"), "TextSecondary");
            var (ok, error) = await _recorder.StartAsync(options);
            SetBusy(false);
            if (!ok)
            {
                SetStatus(T("Str_Recorder_StartFailed", "Couldn't start recording: ") + error, "StatusErrorFg");
                ShowReady(keepStatus: true);
                return;
            }

            _recordingTarget = target;
            _recordingRegion = region;
            SetRecordingUi(true);
            _clock.Start();
            ShowBar();
            var main = Window.GetWindow(this);
            if (_settings.MinimizeWhileRecording && main is { WindowState: not WindowState.Minimized })
            {
                main.WindowState = WindowState.Minimized;
                _minimizedByUs = true;
            }
        }

        private async Task StopAsync()
        {
            SetBusy(true);
            _clock.Stop();
            var result = await _recorder.StopAsync();
            SetBusy(false);
            Finish(result);
        }

        private void OnEndedUnexpectedly(RecordingResult result)
        {
            if (_closed) return;
            _clock.Stop();
            Finish(result, endedByItself: true);
        }

        private void Finish(RecordingResult result, bool endedByItself = false)
        {
            CloseBar();
            SetRecordingUi(false);
            RestoreWindow();

            if (result.Ok)
            {
                var entry = new RecordingEntry
                {
                    Path = result.Path,
                    Name = Path.GetFileName(result.Path),
                    Bytes = result.Bytes,
                    Duration = result.Duration,
                    When = DateTime.Now,
                    DurationText = ScreenRecorderManager.FormatElapsed(result.Duration),
                    SizeText = OutputFolderManager.FormatBytes(result.Bytes),
                };
                _recordings.Insert(0, entry);
                GridRecordings.SelectedItem = entry;
                string head = endedByItself ? T("Str_Recorder_EndedByItself", "Recording ended by itself and was saved.") : T("Str_Recorder_Saved", "Saved.");
                SetStatus($"{head} {entry.Name}" + (result.Message.Length > 0 ? " · " + result.Message : ""), "StatusSuccessFg");
            }
            else
            {
                SetStatus(T("Str_Recorder_Failed", "The recording wasn't saved. ") + result.Message, "StatusErrorFg");
            }
            TxtTimer.Text = ScreenRecorderManager.FormatElapsed(result.Duration);
            ShowReady(keepStatus: true);
        }

        private void RestoreWindow()
        {
            var main = Window.GetWindow(this);
            if (main == null || !_minimizedByUs) return;
            _minimizedByUs = false;
            if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
            main.Show();
            main.Activate();
        }

        // =================================================================
        //  The recording bar and the timer
        // =================================================================

        private void ShowBar()
        {
            CloseBar();
            // The screen button is only for a whole-screen recording; a window or an area has nothing to switch to.
            _bar = _recordingTarget == RecordTarget.Screen
                ? new ScreenRecorderControlWindow(ScreenRecorderManager.ListScreens(), _recordingRegion)
                : new ScreenRecorderControlWindow();
            _bar.ScreenChosen += async screen => await SwitchScreenAsync(screen);
            _bar.StopRequested += async () =>
            {
                if (!_busy && _recorder.IsRecording) await StopAsync();
            };
            _bar.Show();
        }

        private async Task SwitchScreenAsync(RecordScreenInfo screen)
        {
            if (_busy || !_recorder.IsRecording) return;
            SetBusy(true);
            SetStatus(T("Str_Recorder_SwitchingScreen", "Switching screen…"), "TextSecondary");
            var (ok, error) = await _recorder.SwitchScreenAsync(screen.Region);
            SetBusy(false);
            if (ok)
            {
                _recordingRegion = screen.Region;
                SetStatus(T("Str_Recorder_RecordingNow", "Recording… press Stop on the bar when you're done."), "TextSecondary");
            }
            else
            {
                SetStatus(T("Str_Recorder_SwitchFailed", "Couldn't switch screens: ") + error, "StatusErrorFg");
            }
            _bar?.SetCurrentRegion(_recordingRegion);
        }

        /// <summary>A Windows security prompt (UAC, lock screen) opened or closed: recording pauses, and that time is left out of the video.</summary>
        private void OnPausedChanged(bool paused)
        {
            if (_closed || !_recorder.IsRecording) return;
            _bar?.SetPaused(paused);
            if (paused)
            {
                SetPill("Warning", T("Str_Recorder_StatePaused", "Paused"), PackIconKind.PauseCircle);
                SetStatus(T("Str_Recorder_PausedSecurity", "Paused for a Windows security prompt"), "StatusWarningFg");
            }
            else
            {
                SetPill("Error", T("Str_Recorder_StateRecording", "Recording"), PackIconKind.RecordCircle);
                SetStatus(T("Str_Recorder_RecordingNow", "Recording… press Stop on the bar when you're done."), "TextSecondary");
            }
        }

        private void CloseBar()
        {
            var bar = _bar;
            _bar = null;
            bar?.Close();
        }

        private void UpdateTimer()
        {
            string text = ScreenRecorderManager.FormatElapsed(_recorder.Elapsed);
            TxtTimer.Text = text;
            _bar?.SetElapsed(text);
        }

        // =================================================================
        //  Screen state
        // =================================================================

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BtnRecord.IsEnabled = !busy && ScreenRecorderManager.IsAvailable;
        }

        private void SetRecordingUi(bool recording)
        {
            OptionsScrollViewer.IsEnabled = !recording;
            OutputCard.IsLocked = recording;
            IconRecord.Kind = recording ? PackIconKind.Stop : PackIconKind.RecordCircleOutline;
            TxtRecordLabel.SetResourceReference(TextBlock.TextProperty, recording ? "Str_Recorder_StopRecording" : "Str_Recorder_Start");
            SetPill(recording ? "Error" : "Info", recording ? T("Str_Recorder_StateRecording", "Recording") : T("Str_Recorder_StateReady", "Ready"), recording ? PackIconKind.RecordCircle : PackIconKind.CircleOutline);
            if (recording)
            {
                TxtTimer.Text = "00:00";
                SetStatus(T("Str_Recorder_RecordingNow", "Recording… press Stop on the bar when you're done."), "TextSecondary");
            }
        }

        private void ShowReady(bool keepStatus = false)
        {
            SetPill("Info", T("Str_Recorder_StateReady", "Ready"), PackIconKind.CircleOutline);
            if (!keepStatus) SetStatus(T("Str_Recorder_ReadyHint", "Choose what to record, then press Start recording."), "TextSecondary");
        }

        private void SetPill(string kind, string text, PackIconKind icon)
        {
            TxtState.Text = text;
            PillState.SetResourceReference(Border.BackgroundProperty, $"Status{kind}Bg");
            IconState.SetResourceReference(PackIcon.ForegroundProperty, $"Status{kind}Fg");
            TxtState.SetResourceReference(TextBlock.ForegroundProperty, $"Status{kind}Fg");
            IconState.Kind = icon;
        }

        private void SetStatus(string text, string colorKey)
        {
            TxtStatus.Text = text;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            TxtStatus.ToolTip = text;
        }

        // =================================================================
        //  The list of recordings
        // =================================================================

        private static RecordingEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as RecordingEntry;

        private void BtnPlay_Click(object sender, RoutedEventArgs e) => Open(EntryOf(sender));

        private void GridRecordings_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Open(GridRecordings.SelectedItem as RecordingEntry);

        private static void Open(RecordingEntry? entry)
        {
            if (entry == null || !File.Exists(entry.Path)) return;
            try
            {
                Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnShowFile_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is not { } entry || !File.Exists(entry.Path)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.Path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Explorer couldn't start; nothing else to do.
            }
        }

        /// <summary>Takes the recording off this list only. The video file stays where it is.</summary>
        private void BtnRemoveEntry_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { } entry) _recordings.Remove(entry);
        }
    }
}
