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
            (s.Mode switch { TimerMode.Countdown => RadioCountdown, TimerMode.Pomodoro => RadioPomodoro, _ => RadioStopwatch }).IsChecked = true;
            _applying = false;
            SetMode(s.Mode);
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
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
            if (_state != RunState.Idle && System.Windows.MessageBox.Show("Switching modes stops what is running. Switch?", "Timer & Stopwatch", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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
            CardCountdown.Visibility = mode == TimerMode.Countdown ? Visibility.Visible : Visibility.Collapsed;
            CardPomodoro.Visibility = mode == TimerMode.Pomodoro ? Visibility.Visible : Visibility.Collapsed;
            LapArea.Visibility = BtnLap.Visibility = mode == TimerMode.Stopwatch ? Visibility.Visible : Visibility.Collapsed;
            InfoCard.Visibility = mode == TimerMode.Stopwatch ? Visibility.Collapsed : Visibility.Visible;
            TxtInfoBody.Text = mode == TimerMode.Countdown
                ? "Type a time such as 5:00, 90s or 1h30m in the sidebar (or click a preset), then press Start. When it reaches zero you get a sound and a message here."
                : "Work for a set time, then take a short break; after every few work sessions you get a long break. The next phase starts by itself, so you only have to press Start once.";
            TxtSessions.Text = mode == TimerMode.Pomodoro ? "Work sessions finished: 0" : "";
            BarProgress.Visibility = mode == TimerMode.Stopwatch ? Visibility.Collapsed : Visibility.Visible;
            _completedWork = 0;
            _phase = PomodoroPhase.Work;
            ShowIdle();
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

        private static string PhaseName(PomodoroPhase p) => p switch { PomodoroPhase.ShortBreak => "Short break", PomodoroPhase.LongBreak => "Long break", _ => "Work" };

        /// <summary>The clock as it looks before anything has started.</summary>
        private void ShowIdle()
        {
            TxtMessage.Text = "";
            TxtMessage.SetResourceReference(TextBlock.ForegroundProperty, "StatusSuccessFg");
            BarProgress.Value = 0;
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
                    TxtMessage.Text = ok || TxtCountdown.Text.Length == 0 ? "" : "I can't read that time. Try 5:00, 90s or 1h30m.";
                    TxtMessage.SetResourceReference(TextBlock.ForegroundProperty, ok ? "StatusSuccessFg" : "StatusErrorFg");
                    BtnStart.IsEnabled = ok;
                    break;
                default:
                    TxtPhase.Text = PhaseName(_phase);
                    TxtTime.Text = TimerStopwatchManager.Format(PhaseLength(_phase), false);
                    TxtSessions.Text = $"Work sessions finished: {_completedWork}";
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
                return;
            }

            var left = _total - _clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                Finished();
                return;
            }
            TxtTime.Text = TimerStopwatchManager.Format(TimerStopwatchManager.RoundUpToSecond(left), false);
            BarProgress.Value = 1 - left.TotalSeconds / _total.TotalSeconds;
        }

        private void Finished()
        {
            if (ChkSound.IsChecked == true) System.Media.SystemSounds.Exclamation.Play();

            if (_mode == TimerMode.Countdown)
            {
                _tick.Stop();
                _clock.Reset();
                _state = RunState.Idle;
                TxtTime.Text = TimerStopwatchManager.Format(TimeSpan.Zero, false);
                BarProgress.Value = 1;
                TxtMessage.Text = "Time's up!";
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
            TxtSessions.Text = $"Work sessions finished: {_completedWork}";
            TxtMessage.Text = finished == PomodoroPhase.Work
                ? $"Work session {_completedWork} done - time for a {(_phase == PomodoroPhase.LongBreak ? "long" : "short")} break."
                : "Break over - back to work.";
            BarProgress.Value = 0;
            TxtTime.Text = TimerStopwatchManager.Format(_total, false);
        }

        private void SetButtons()
        {
            bool running = _state == RunState.Running;
            TxtStart.Text = running ? "Pause" : _state == RunState.Paused ? "Resume" : "Start";
            IconStart.Kind = running ? PackIconKind.Pause : PackIconKind.Play;
            BtnLap.IsEnabled = running && _mode == TimerMode.Stopwatch;
            // The settings can't change under a run.
            SidebarStack.IsEnabled = _state == RunState.Idle;
            if (_mode == TimerMode.Pomodoro && _state != RunState.Idle) TxtPhase.Text = PhaseName(_phase);
        }

        /// <summary>Called when the window closes: stops the clock and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _tick.Stop();
            _clock.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
