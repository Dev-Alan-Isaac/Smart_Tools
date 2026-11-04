using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Audio Recorder - UI only. Recording, pausing, hearing the input and
    /// playback are in <see cref="MicRecorderManager"/>; names, saving and
    /// settings in MicRecorderManager.Files.cs.
    /// </summary>
    public partial class MicRecorderView : UserControl
    {
        private static string DialogTitle => Loc.T("Str_Tool_audio-recorder_Name", "Audio Recorder");
        private const double SidebarExpandedWidth = 290;
        private const double SidebarCollapsedWidth = 56;

        private readonly MicRecorderManager _manager = new();
        private readonly ObservableCollection<MicRecorderTake> _takes = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
        private readonly bool _viewConstructed;

        private MicRecorderSettings _settings = new();
        // Inside the Output card (built in BuildOutputOptions).
        private TextBox TxtOutputFileName = null!;
        private TextBlock TxtFileNameExample = null!;
        private CheckBox AutoSaveCheckBox = null!;
        private double _level;          // written on the capture thread, read by the timer
        private double _shownLevel;
        private int _ticks;
        private bool _loadedOnce;
        private bool _applying;
        private bool _closed;
        private bool _optionsCollapsed;

        public MicRecorderView()
        {
            InitializeComponent();
            ListBoxTakes.ItemsSource = _takes;
            _takes.CollectionChanged += (_, _) => UpdateTakesUi();

            _manager.LevelChanged += level => _level = level;
            _manager.RecordingFinished += Manager_RecordingFinished;
            _manager.PlaybackEnded += _ => UpdateTakesUi();
            _timer.Tick += Timer_Tick;
            OutputCard.DefaultFolder = MicRecorderManager.DefaultFolder;
            BuildOutputOptions();
            _viewConstructed = true;
        }

        /// <summary>The Output card's folder (this session only - never saved), else the default.</summary>
        private string SaveFolder => OutputCard.EffectiveFolder ?? MicRecorderManager.DefaultFolder;

        private void BuildOutputOptions()
        {
            TxtOutputFileName = OutputCardOptions.TextBox("Recording + date and time",
                "\".wav\" is added if missing. A name that's taken gets \" (2)\".");
            TxtOutputFileName.TextChanged += TxtOutputFileName_TextChanged;
            TxtFileNameExample = OutputCardOptions.Hint();
            AutoSaveCheckBox = OutputCardOptions.CheckBox("Save without asking", true,
                "Save straight into the folder above. Off: recordings wait in the list until you save them.");
            AutoSaveCheckBox.Checked += Option_Changed;
            AutoSaveCheckBox.Unchecked += Option_Changed;
            OutputCard.Options = OutputCardOptions.Panel(
                OutputCardOptions.Label("File name"), TxtOutputFileName, TxtFileNameExample, AutoSaveCheckBox);
        }

        private void OutputCard_FolderChanged(object? sender, EventArgs e)
        {
            if (_viewConstructed) UpdateFileNameExample();
        }

        /// <summary>
        /// Called from MainWindow.Window_Closing. A recording in progress is
        /// finished and saved into the save folder (the old view threw it
        /// away when you left the page), then the devices are released.
        /// </summary>
        public void CancelActiveRun()
        {
            if (_closed) return;
            _closed = true;
            _timer.Stop();
            _manager.StopPlayback();
            var take = _manager.StopNow();
            if (take != null)
            {
                try
                {
                    MicRecorderManager.SaveTake(take, SaveFolder, TxtOutputFileName.Text, take.RecordedAt);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Info($"Recording left in {take.FilePath}: {ex.Message}");
                }
            }
            _manager.Dispose();
        }

        // =================================================================
        //  Lifetime and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            MicRecorderManager.CleanOldTempTakes(DateTime.Now);
            ApplySettings(MicRecorderManager.LoadSettings());
            LoadDevices();
            _loadedOnce = true;
            UpdateTransport();
            UpdateTakesUi();
        }

        /// <summary>Switching to another tool stops playback only - a recording carries on and is here when you come back.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_closed) _manager.StopPlayback();
        }

        private void ApplySettings(MicRecorderSettings s)
        {
            _applying = true;
            try
            {
                _settings = s;
                SampleRateCombo.SelectedItem = SampleRateCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (string)i.Tag == s.SampleRate.ToString()) ?? SampleRateCombo.Items[2];
                RadioStereo.IsChecked = s.Channels == 2;
                RadioMono.IsChecked = s.Channels != 2;
                Radio24Bit.IsChecked = s.BitsPerSample == 24;
                Radio16Bit.IsChecked = s.BitsPerSample != 24;
                MonitorInputCheckBox.IsChecked = s.MonitorInput;
                AutoSaveCheckBox.IsChecked = s.SaveWithoutAsking;
                TxtOutputFileName.Text = s.FileName;
            }
            finally
            {
                _applying = false;
            }
            UpdateFormatUi();
            UpdateFileNameExample();
        }

        private void SaveSettings()
        {
            if (_applying || !_loadedOnce || _closed) return;
            var f = SelectedFormat();
            _settings.SampleRate = f.SampleRate;
            _settings.Channels = f.Channels;
            _settings.BitsPerSample = f.BitsPerSample;
            _settings.MonitorInput = MonitorInputCheckBox.IsChecked == true;
            _settings.SaveWithoutAsking = AutoSaveCheckBox.IsChecked == true;
            _settings.FileName = TxtOutputFileName.Text.Trim();
            if (DeviceCombo.SelectedItem is MicRecorderDevice device) _settings.DeviceName = device.Name;
            try
            {
                MicRecorderManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Recorder settings not saved: {ex.Message}");
            }
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
            TxtDeviceHint.Text = any
                ? "Use headphones to hear yourself - speakers feed back into the microphone."
                : "No microphone found. Plug one in, then press Look again (top right).";
            TxtDeviceHint.SetResourceReference(TextBlock.ForegroundProperty, any ? "SidebarTextMuted" : "StatusErrorFg");
            UpdateTransport();
        }

        private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            SaveSettings();
        }

        private void MonitorInput_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            if (_manager.State != MicRecorderState.Idle) _manager.SetMonitor(MonitorInputCheckBox.IsChecked == true);
            SaveSettings();
        }

        private MicRecorderFormat SelectedFormat()
        {
            int rate = SampleRateCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int r) ? r : 44100;
            return new MicRecorderFormat(rate, RadioStereo.IsChecked == true ? 2 : 1, Radio24Bit.IsChecked == true ? 24 : 16);
        }

        private void Format_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            UpdateFormatUi();
            SaveSettings();
        }

        private void UpdateFormatUi()
        {
            var f = SelectedFormat();
            TxtFormat.Text = f.Describe();
            TxtFormatHint.Text = $"About {MicRecorderText.Bytes((long)f.BytesPerSecond * 60)} a minute.";
        }

        private void TxtOutputFileName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateFileNameExample();
            SaveSettings();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateFileNameExample();
            SaveSettings();
        }

        private void UpdateFileNameExample()
        {
            string name = MicRecorderManager.TakeFileName(TxtOutputFileName.Text, DateTime.Now);
            TxtFileNameExample.Text = AutoSaveCheckBox.IsChecked == true
                ? $"Each recording is saved as \"{name}\" in the folder above."                : "Recordings wait in the list - save the ones you want to keep.";
        }

        // =================================================================
        //  Recording
        // =================================================================

        /// <summary>The big round button: start, or stop and keep.</summary>
        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            switch (_manager.State)
            {
                case MicRecorderState.Idle:
                    StartRecording();
                    break;
                case MicRecorderState.Recording:
                case MicRecorderState.Paused:
                    _manager.Stop();
                    StatusText.Text = "Finishing…";
                    UpdateTransport();
                    break;
            }
        }

        private void StartRecording()
        {
            if (DeviceCombo.SelectedItem is not MicRecorderDevice device)
            {
                ShowStatus("No microphone to record from.", error: true);
                return;
            }
            _manager.StopPlayback();
            var format = SelectedFormat();
            try
            {
                _manager.Start(device, format, MonitorInputCheckBox.IsChecked == true);
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                ShowStatus($"Couldn't start recording: {ex.Message} The microphone may be in use by another app, or blocked in Windows' privacy settings.", error: true);
                return;
            }
            _ticks = 0;
            _timer.Start();
            ShowStatus($"Recording from {device.Name}…");
            UiMotion.Pop(BtnRecord, 0.9);
            UpdateTransport();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.State == MicRecorderState.Recording)
            {
                _manager.Pause();
                ShowStatus("Paused - nothing is being recorded. Resume to carry on in the same file.");
            }
            else if (_manager.State == MicRecorderState.Paused)
            {
                _manager.Resume();
                ShowStatus("Recording…");
            }
            UpdateTransport();
        }

        /// <summary>The driver has handed over the last buffers: the take is complete.</summary>
        private void Manager_RecordingFinished(MicRecorderTake? take, string? error)
        {
            if (_closed) return;
            _timer.Stop();
            _level = 0;
            SetLevel(0);

            if (take != null)
            {
                string message = $"Recorded {MicRecorderText.Duration(take.Duration)}.";
                if (AutoSaveCheckBox.IsChecked == true)
                {
                    try
                    {
                        string path = MicRecorderManager.SaveTake(take, SaveFolder, TxtOutputFileName.Text, take.RecordedAt);
                        message = $"Saved {Path.GetFileName(path)} ({MicRecorderText.Duration(take.Duration)}).";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        error ??= $"Couldn't save into {SaveFolder}: {ex.Message} It's in the list - use Save as.";
                    }
                    OutputCard.Refresh();   // free space, or "made on the first save" is now "ready"
                }
                _takes.Insert(0, take);
                ListBoxTakes.SelectedItem = take;
                ListBoxTakes.ScrollIntoView(take);
                ShowStatus(error ?? message, error != null);
            }
            else
            {
                ShowStatus(error ?? "Nothing was recorded.", error != null);
            }
            UpdateTransport();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            // Meter: rise at once, fall gently - easier to read than raw buffer peaks.
            double target = _level;
            _shownLevel = target > _shownLevel ? target : Math.Max(target, _shownLevel - 3);
            SetLevel(_shownLevel);

            TxtElapsed.Text = MicRecorderText.Duration(_manager.Recorded);
            if (_manager.State == MicRecorderState.Recording && ++_ticks % 16 == 0)
                UiMotion.Pop(RecDot, 0.6);   // a heartbeat about once a second
        }

        private void SetLevel(double percent)
        {
            LevelScale.ScaleX = Math.Clamp(percent / 100, 0, 1);
            string brush = percent >= 95 ? "StatusErrorFg" : percent >= 80 ? "StatusWarningFg" : "StatusSuccessFg";
            LevelFill.SetResourceReference(Border.BackgroundProperty, brush);
        }

        private void UpdateTransport()
        {
            var state = _manager.State;
            bool idle = state == MicRecorderState.Idle;
            bool hasDevice = DeviceCombo.SelectedItem is MicRecorderDevice;

            BtnRecord.IsEnabled = state != MicRecorderState.Finishing && (!idle || hasDevice);
            IconRecord.Kind = idle ? PackIconKind.Microphone : PackIconKind.Stop;
            BtnRecord.ToolTip = idle ? "Start recording" : "Stop and keep the recording";

            BtnPause.IsEnabled = state is MicRecorderState.Recording or MicRecorderState.Paused;
            IconPause.Kind = state == MicRecorderState.Paused ? PackIconKind.Record : PackIconKind.Pause;
            TxtPauseLabel.Text = state == MicRecorderState.Paused ? "Resume" : "Pause";

            RecDot.Opacity = state == MicRecorderState.Recording ? 1 : 0.25;
            if (idle) TxtElapsed.Text = "0:00";

            // Format and device can't change mid-recording.
            DeviceCombo.IsEnabled = idle;
            BtnRefreshDevices.IsEnabled = idle;
            SampleRateCombo.IsEnabled = idle;
            RadioMono.IsEnabled = RadioStereo.IsEnabled = idle;
            Radio16Bit.IsEnabled = Radio24Bit.IsEnabled = idle;
        }

        private void ShowStatus(string text, bool error = false)
        {
            StatusText.Text = text;
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "TextSecondary");
        }

        // =================================================================
        //  Recordings list
        // =================================================================

        private void UpdateTakesUi()
        {
            int count = _takes.Count;
            TakesEmptyPanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountPill.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtCount.Text = count.ToString("N0");
            BtnClearTakes.IsEnabled = count > 0;
        }

        private static MicRecorderTake? TakeOf(object sender) => (sender as Button)?.Tag as MicRecorderTake;

        private void BtnPlayTake_Click(object sender, RoutedEventArgs e)
        {
            if (TakeOf(sender) is { } take) TogglePlay(take);
        }

        private void ListBoxTakes_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListBoxTakes.SelectedItem is MicRecorderTake take) TogglePlay(take);
        }

        private void TogglePlay(MicRecorderTake take)
        {
            if (take.IsPlaying)
            {
                _manager.StopPlayback();
                return;
            }
            if (!File.Exists(take.FilePath))
            {
                ShowStatus($"{take.DisplayName} is no longer there.", error: true);
                return;
            }
            try
            {
                _manager.Play(take);
            }
            catch (Exception ex) when (ex is NAudio.MmException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                ShowStatus($"Couldn't play it: {ex.Message}", error: true);
            }
        }

        private void BtnSaveTake_Click(object sender, RoutedEventArgs e)
        {
            if (TakeOf(sender) is not { } take || !File.Exists(take.FilePath)) return;
            var dialog = new SaveFileDialog
            {
                Title = "Save recording",
                Filter = "WAV audio|*.wav",
                DefaultExt = ".wav",
                FileName = take.IsSaved ? take.DisplayName : MicRecorderManager.TakeFileName(TxtOutputFileName.Text, take.RecordedAt),
            };
            string folder = take.IsSaved ? take.Folder : SaveFolder;
            if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            _manager.Release(take);
            try
            {
                MicRecorderManager.SaveTakeAs(take, dialog.FileName);
                ShowStatus($"Saved {Path.GetFileName(dialog.FileName)}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this)!, $"Couldn't save: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnShowTake_Click(object sender, RoutedEventArgs e)
        {
            if (TakeOf(sender) is { } take && ShellExplorer.Show(take.FilePath) == ShellExplorer.ShowResult.NotFound)
                ShowStatus($"{take.DisplayName} is no longer there.", error: true);
        }

        /// <summary>A saved file stays where it is; an unsaved one is gone for good, so that asks first.</summary>
        private void BtnRemoveTake_Click(object sender, RoutedEventArgs e)
        {
            if (TakeOf(sender) is not { } take) return;
            if (!take.IsSaved &&
                MessageBox.Show(Window.GetWindow(this)!, $"\"{take.DisplayName}\" hasn't been saved. Throw it away?", DialogTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            _manager.Release(take);
            MicRecorderManager.DeleteIfTemp(take.FilePath);
            _takes.Remove(take);
        }

        private void BtnClearTakes_Click(object sender, RoutedEventArgs e)
        {
            if (_takes.Count == 0) return;
            int unsaved = _takes.Count(t => !t.IsSaved);
            string question = unsaved == 0
                ? "Empty the list? The saved files stay where they are."
                : $"{unsaved:N0} {MicRecorderText.Plural(unsaved, "recording hasn't", "recordings haven't")} been saved and will be thrown away. Empty the list?";
            if (MessageBox.Show(Window.GetWindow(this)!, question, DialogTitle, MessageBoxButton.YesNo,
                    unsaved == 0 ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            _manager.StopPlayback();
            foreach (var take in _takes) MicRecorderManager.DeleteIfTemp(take.FilePath);
            _takes.Clear();
        }
    }
}
