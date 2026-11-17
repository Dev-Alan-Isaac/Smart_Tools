using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using System.Collections.ObjectModel;
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
    /// Timer &amp; Stopwatch - UI only. A stopwatch with laps, a countdown, and a Pomodoro cycle that moves from work to
    /// break by itself. Typed times and the cycle rules are in <see cref="TimerStopwatchManager"/>.
    /// </summary>
    public partial class TimerStopwatchView : UserControl
    {
        private sealed record Lap(int Number, string Split, string Total);

        private enum RunState { Idle, Running, Paused }

        private readonly ObservableCollection<Lap> _laps = new();
        private readonly Stopwatch _clock = new();
        private readonly DispatcherTimer _tick;
        private readonly DispatcherTimer _saveTimer;
        private readonly bool _viewConstructed;
        private readonly TimerAlertPlayer _alert = new();
        private string? _soundFile;

        private bool _loadedOnce;
        private bool _applying;
        private TimerMode _mode = TimerMode.Stopwatch;
        private RunState _state = RunState.Idle;
        private TimeSpan _total;                       // length of the countdown / phase being run
        private TimeSpan _lastLap = TimeSpan.Zero;
        private PomodoroPhase _phase = PomodoroPhase.Work;
        private int _completedWork;

        public TimerStopwatchView()
        {
            InitializeComponent();
            LapList.ItemsSource = _laps;
            _laps.CollectionChanged += (_, _) => TxtLapEmpty.Visibility = _laps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _tick.Tick += (_, _) => Tick();
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _alert.Ended += () => IconTestSound.Kind = PackIconKind.VolumeHigh;
            foreach (var c in TimerAlertPlayer.Choices)
                CmbSound.Items.Add(new ComboBoxItem { Content = Loc.T(c.LanguageKey, c.Fallback), Tag = c.Id });
            _viewConstructed = true;
        }

        // =================================================================
        //  Start-up, settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            var s = TimerStopwatchManager.Clamp(JsonSettings.Load<TimerStopwatchSettings>(TimerStopwatchManager.SettingsName));
            _applying = true;
            TxtCountdown.Text = s.Countdown;
            TxtWork.Text = s.WorkMinutes.ToString(CultureInfo.InvariantCulture);
            TxtShort.Text = s.ShortBreakMinutes.ToString(CultureInfo.InvariantCulture);
            TxtLong.Text = s.LongBreakMinutes.ToString(CultureInfo.InvariantCulture);
            TxtEvery.Text = s.LongBreakEvery.ToString(CultureInfo.InvariantCulture);
            ChkSound.IsChecked = s.PlaySound;
            _soundFile = s.SoundFile;
            CmbSound.SelectedIndex = Math.Max(0, TimerAlertPlayer.Choices.ToList().FindIndex(c => c.Id == s.Sound));
            UpdateSoundUi();
            (s.Mode switch { TimerMode.Countdown => RadioCountdown, TimerMode.Pomodoro => RadioPomodoro, _ => RadioStopwatch }).IsChecked = true;
            _applying = false;
            SetMode(s.Mode);
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            _alert.Stop();
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private TimerStopwatchSettings ReadSettings()
        {
            int Num(string t, int fallback) => int.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : fallback;
            return TimerStopwatchManager.Clamp(new TimerStopwatchSettings
            {
                Mode = _mode,
                Countdown = TxtCountdown.Text,
                WorkMinutes = Num(TxtWork.Text, 25),
                ShortBreakMinutes = Num(TxtShort.Text, 5),
                LongBreakMinutes = Num(TxtLong.Text, 15),
                LongBreakEvery = Num(TxtEvery.Text, 4),
                PlaySound = ChkSound.IsChecked == true,
                Sound = SoundId,
                SoundFile = _soundFile,
            });
        }

        private void SaveSettings() => JsonSettings.Save(TimerStopwatchManager.SettingsName, ReadSettings());

        private void QueueSave()
        {
            if (!_viewConstructed || _applying) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Setting_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            QueueSave();
            if (_state == RunState.Idle) ShowIdle();
        }

        private void Setting_Click(object sender, RoutedEventArgs e) => QueueSave();

        // =================================================================
        //  Alert sound
        // =================================================================

        private string SoundId => (CmbSound.SelectedItem as ComboBoxItem)?.Tag as string ?? TimerAlertPlayer.DefaultId;

        private void UpdateSoundUi()
        {
            bool file = SoundId == TimerAlertPlayer.FileId;
            BtnPickSound.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
            TxtSoundFile.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
            TxtSoundFile.Text = string.IsNullOrEmpty(_soundFile) ? Loc.T("Str_Timer_SndNoFile", "No file chosen yet - the Windows alert plays until you pick one.") : _soundFile;
            TxtSoundFile.ToolTip = _soundFile;
        }

        private void Sound_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            _alert.Stop();
            IconTestSound.Kind = PackIconKind.VolumeHigh;
            UpdateSoundUi();
            QueueSave();
            if (SoundId == TimerAlertPlayer.FileId && string.IsNullOrEmpty(_soundFile)) BtnPickSound_Click(sender, e);
            else PlayTest();
        }

        private void BtnPickSound_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("Str_Timer_SndPick", "Choose a sound or song"),
                Filter = TimerAlertPlayer.FileFilter,
            };
            if (!string.IsNullOrEmpty(_soundFile) && System.IO.File.Exists(_soundFile)) dlg.InitialDirectory = System.IO.Path.GetDirectoryName(_soundFile);
            if (dlg.ShowDialog() != true) return;
            _soundFile = dlg.FileName;
            UpdateSoundUi();
            QueueSave();
            PlayTest();
        }

        private void BtnTestSound_Click(object sender, RoutedEventArgs e)
        {
            if (IconTestSound.Kind == PackIconKind.Stop)
            {
                _alert.Stop();
                IconTestSound.Kind = PackIconKind.VolumeHigh;
                return;
            }
            PlayTest();
        }

        private void PlayTest()
        {
            _alert.Play(SoundId, _soundFile);
            IconTestSound.Kind = PackIconKind.Stop;
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (_state != RunState.Idle || (sender as FrameworkElement)?.Tag is not string t) return;
            TxtCountdown.Text = t;
        }

        // =================================================================
        //  Modes
        // =================================================================

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse(tag, out TimerMode mode)) return;
            if (mode == _mode) return;
            if (_state != RunState.Idle && ThemedMessageBox.Show(Loc.T("Str_MB_SwitchingModesStopsWhat", "Switching modes stops what is running. Switch?"), Loc.T("Str_Tool_timer-stopwatch_Name", "Timer & Stopwatch"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                (_mode switch { TimerMode.Countdown => RadioCountdown, TimerMode.Pomodoro => RadioPomodoro, _ => RadioStopwatch }).IsChecked = true;
                return;
            }
            Reset();
            SetMode(mode);
            QueueSave();
        }

        private void SetMode(TimerMode mode)
        {
            _mode = mode;
            OptionsCard.Visibility = mode == TimerMode.Stopwatch ? Visibility.Collapsed : Visibility.Visible;
            CardCountdown.Visibility = mode == TimerMode.Countdown ? Visibility.Visible : Visibility.Collapsed;
            CardPomodoro.Visibility = mode == TimerMode.Pomodoro ? Visibility.Visible : Visibility.Collapsed;
            LapArea.Visibility = mode == TimerMode.Stopwatch ? Visibility.Visible : Visibility.Collapsed;
            BtnLap.Visibility = mode == TimerMode.Stopwatch ? Visibility.Visible : Visibility.Hidden;   // keeps its room: Start stays centred under the ring
            InfoCard.Visibility = mode == TimerMode.Stopwatch ? Visibility.Collapsed : Visibility.Visible;
            TxtInfoBody.Text = mode == TimerMode.Countdown
                ? Loc.T("Str_Timer_MsgInfoCountdown", "Type a time such as 5:00, 90s or 1h30m in the sidebar (or click a preset), then press Start. When it reaches zero you get a sound and a message here.")
                : Loc.T("Str_Timer_MsgInfoPomodoro", "Work for a set time, then take a short break; after every few work sessions you get a long break. The next phase starts by itself, so you only have to press Start once.");
            TxtSessions.Text = mode == TimerMode.Pomodoro ? Loc.F("Str_Timer_MsgSessions", "Work sessions finished: {0}", 0) : "";
            _completedWork = 0;
            _phase = PomodoroPhase.Work;
            ShowIdle();
        }

        /// <summary>The ring's colour: the accent for the stopwatch, a countdown and work; green and blue for the two breaks.</summary>
        private void RingColor(string resourceKey) => BarProgress.SetResourceReference(Controls.RingProgress.StrokeProperty, resourceKey);

        private static string PhaseKey(PomodoroPhase phase) => phase switch
        {
            PomodoroPhase.ShortBreak => "StatusSuccessFg",
            PomodoroPhase.LongBreak => "StatusInfoFg",
            _ => "AccentColor",
        };

        /// <summary>Pomodoro: one dot per work session before the long break, filled as sessions are finished.</summary>
        private void UpdateDots()
        {
            bool show = _mode == TimerMode.Pomodoro;
            DotsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            int every = Math.Clamp(ReadSettings().LongBreakEvery, 1, 12);
            int filled = _completedWork % every;
            if (filled == 0 && _completedWork > 0 && _phase == PomodoroPhase.LongBreak) filled = every;
            DotsPanel.Children.Clear();
            for (int i = 0; i < every; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 11, Height = 11, Margin = new Thickness(4, 0, 4, 0) };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, i < filled ? "AccentColor" : "RowBg");
                DotsPanel.Children.Add(dot);
            }
        }

        private TimeSpan PhaseLength(PomodoroPhase phase)
        {
            var s = ReadSettings();
            return TimeSpan.FromMinutes(phase switch
            {
                PomodoroPhase.ShortBreak => s.ShortBreakMinutes,
                PomodoroPhase.LongBreak => s.LongBreakMinutes,
                _ => s.WorkMinutes,
            });
        }

        private static string PhaseName(PomodoroPhase p) => p switch { PomodoroPhase.ShortBreak => Loc.T("Str_Timer_PhaseShort", "Short break"), PomodoroPhase.LongBreak => Loc.T("Str_Timer_PhaseLong", "Long break"), _ => Loc.T("Str_Timer_PhaseWork", "Work") };

        /// <summary>The clock as it looks before anything has started.</summary>
        private void ShowIdle()
        {
            TxtMessage.Text = "";
            TxtMessage.SetResourceReference(TextBlock.ForegroundProperty, "StatusSuccessFg");
            BarProgress.Value = _mode == TimerMode.Stopwatch ? 0 : 1;   // a timer starts with a full ring that empties
            RingColor(_mode == TimerMode.Pomodoro ? PhaseKey(_phase) : "AccentColor");
            UpdateDots();
            switch (_mode)
            {
                case TimerMode.Stopwatch:
                    TxtPhase.Text = "";
                    TxtTime.Text = TimerStopwatchManager.Format(TimeSpan.Zero, true);
                    break;
                case TimerMode.Countdown:
                    TxtPhase.Text = "";
                    bool ok = TimerStopwatchManager.TryParseDuration(TxtCountdown.Text, out var d);
                    TxtTime.Text = ok ? TimerStopwatchManager.Format(d, false) : "--:--";
                    TxtMessage.Text = ok || TxtCountdown.Text.Length == 0 ? "" : Loc.T("Str_Timer_MsgBadTime", "I can't read that time. Try 5:00, 90s or 1h30m.");
                    TxtMessage.SetResourceReference(TextBlock.ForegroundProperty, ok ? "StatusSuccessFg" : "StatusErrorFg");
                    BtnStart.IsEnabled = ok;
                    break;
                default:
                    TxtPhase.Text = PhaseName(_phase);
                    TxtTime.Text = TimerStopwatchManager.Format(PhaseLength(_phase), false);
                    TxtSessions.Text = Loc.F("Str_Timer_MsgSessions", "Work sessions finished: {0}", _completedWork);
                    break;
            }
            if (_mode != TimerMode.Countdown) BtnStart.IsEnabled = true;
            SetButtons();
        }

        // =================================================================
        //  Running
        // =================================================================

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_state == RunState.Running) { Pause(); return; }
            _alert.Stop();
            IconTestSound.Kind = PackIconKind.VolumeHigh;

            if (_state == RunState.Idle)
            {
                if (_mode == TimerMode.Countdown)
                {
                    if (!TimerStopwatchManager.TryParseDuration(TxtCountdown.Text, out _total)) return;
                }
                else if (_mode == TimerMode.Pomodoro) _total = PhaseLength(_phase);
                TxtMessage.Text = "";
                _clock.Restart();
            }
            else _clock.Start();

            _state = RunState.Running;
            _tick.Interval = TimeSpan.FromMilliseconds(_mode == TimerMode.Stopwatch ? 33 : 100);
            _tick.Start();
            SetButtons();
        }

        private void Pause()
        {
            _clock.Stop();
            _tick.Stop();
            _state = RunState.Paused;
            Tick();
            SetButtons();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            Reset();
            ShowIdle();
        }

        private void Reset()
        {
            _alert.Stop();
            IconTestSound.Kind = PackIconKind.VolumeHigh;
            _tick.Stop();
            _clock.Reset();
            _state = RunState.Idle;
            _laps.Clear();
            _lastLap = TimeSpan.Zero;
            if (_mode == TimerMode.Pomodoro) { _phase = PomodoroPhase.Work; _completedWork = 0; }
        }

        private void BtnLap_Click(object sender, RoutedEventArgs e)
        {
            if (_mode != TimerMode.Stopwatch || _state != RunState.Running) return;
            var now = _clock.Elapsed;
            _laps.Insert(0, new Lap(_laps.Count + 1, "+" + TimerStopwatchManager.Format(now - _lastLap, true), TimerStopwatchManager.Format(now, true)));
            _lastLap = now;
        }

        private void Tick()
        {
            if (_mode == TimerMode.Stopwatch)
            {
                TxtTime.Text = TimerStopwatchManager.Format(_clock.Elapsed, true);
                BarProgress.Value = _clock.Elapsed.TotalSeconds % 60 / 60;   // one turn per minute
                return;
            }

            var left = _total - _clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                Finished();
                return;
            }
            TxtTime.Text = TimerStopwatchManager.Format(TimerStopwatchManager.RoundUpToSecond(left), false);
            BarProgress.Value = left.TotalSeconds / _total.TotalSeconds;   // what is left
        }

        private void Finished()
        {
            if (ChkSound.IsChecked == true) _alert.Play(SoundId, _soundFile);

            if (_mode == TimerMode.Countdown)
            {
                _tick.Stop();
                _clock.Reset();
                _state = RunState.Idle;
                TxtTime.Text = TimerStopwatchManager.Format(TimeSpan.Zero, false);
                BarProgress.Value = 1;   // at zero the whole ring turns red
                RingColor("StatusErrorFg");
                TxtMessage.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                TxtMessage.Text = Loc.T("Str_Timer_MsgTimesUp", "Time's up!");
                SetButtons();
                return;
            }

            // Pomodoro: go straight into the next phase.
            var s = ReadSettings();
            var finished = _phase;
            _phase = TimerStopwatchManager.Next(_phase, ref _completedWork, s.LongBreakEvery);
            _total = PhaseLength(_phase);
            _clock.Restart();
            TxtPhase.Text = PhaseName(_phase);
            TxtSessions.Text = Loc.F("Str_Timer_MsgSessions", "Work sessions finished: {0}", _completedWork);
            TxtMessage.Text = finished == PomodoroPhase.Work
                ? (_phase == PomodoroPhase.LongBreak ? Loc.F("Str_Timer_MsgWorkDoneLong", "Work session {0} done - time for a long break.", _completedWork) : Loc.F("Str_Timer_MsgWorkDoneShort", "Work session {0} done - time for a short break.", _completedWork))
                : Loc.T("Str_Timer_MsgBreakOver", "Break over - back to work.");
            BarProgress.Value = 1;   // the next phase starts with a full ring
            RingColor(PhaseKey(_phase));
            UpdateDots();
            TxtTime.Text = TimerStopwatchManager.Format(_total, false);
        }

        private void SetButtons()
        {
            bool running = _state == RunState.Running;
            TxtStart.Text = running ? Loc.T("Str_Timer_BtnPause", "Pause") : _state == RunState.Paused ? Loc.T("Str_Timer_BtnResume", "Resume") : Loc.T("Str_Timer_BtnStart", "Start");
            IconStart.Kind = running ? PackIconKind.Pause : PackIconKind.Play;
            BtnLap.IsEnabled = running && _mode == TimerMode.Stopwatch;
            // The settings can't change under a run.
            SidebarStack.IsEnabled = _state == RunState.Idle;
            if (_mode == TimerMode.Pomodoro && _state != RunState.Idle) TxtPhase.Text = PhaseName(_phase);
            if (_mode == TimerMode.Pomodoro && _state != RunState.Idle) RingColor(PhaseKey(_phase));
        }

        /// <summary>Called when the window closes: stops the clock and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _tick.Stop();
            _clock.Stop();
            _alert.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
