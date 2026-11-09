using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Engines.Midi;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>MIDI Play-Along - UI only. Reading the file and playing it (click, mutes, tempo, loop) are <see cref="MidiPlayAlongManager"/>'s.</summary>
    public partial class MidiPlayAlongView : UserControl
    {
        private readonly MidiPlayAlongManager _manager = new();
        private readonly BulkObservableCollection<MidiPlayAlongFile> _files = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
        private MidiSong? _song;
        private MidiPlayAlongSettings _settings = new();
        private double _pausedTick;
        private bool _scrubbing, _applying, _loaded, _closed;
        private int _fileToken;
        private static readonly string[] Extensions = { ".mid", ".midi", ".rmi" };

        private static string Title => Loc.T("Str_Tool_midi-playalong_Name", "MIDI Play-Along");

        public MidiPlayAlongView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _files;
            InputCard.FileFilterProvider = () => Loc.T("Str_MidiInstruments_MidiFilter", "MIDI file (*.mid)|*.mid") + ";*.midi|" + Loc.T("Str_MidiInstruments_AllFiles", "All files") + "|*.*";
            _timer.Tick += Timer_Tick;
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _manager.Finished += () => Dispatcher.BeginInvoke(OnFinished);
            CmbCountIn.ItemsSource = new[]
            {
                Loc.T("Str_PlayAlong_CountNone", "None"),
                Loc.T("Str_PlayAlong_CountOne", "1 bar"),
                Loc.T("Str_PlayAlong_CountTwo", "2 bars"),
            };
        }

        // =================================================================
        //  Settings and the sound output
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            _closed = false;
            _settings = JsonSettings.Load<MidiPlayAlongSettings>(MidiPlayAlongManager.SettingsName);
            _applying = true;
            ChkClick.IsChecked = _settings.Metronome;
            SliderClick.Value = Math.Clamp(_settings.ClickVolume, 10, 127);
            CmbCountIn.SelectedIndex = Math.Clamp(_settings.CountInBars, 0, 2);
            SliderTempo.Value = Math.Clamp(_settings.TempoPercent, 40, 150);
            ChkLoop.IsChecked = _settings.Loop;

            var outputs = MidiDevices.Outputs();
            CmbOutput.ItemsSource = outputs;
            var wanted = outputs.FirstOrDefault(o => o.Name == _settings.OutputDevice)
                         ?? outputs.FirstOrDefault(o => o.Name.Contains("GS Wavetable", StringComparison.OrdinalIgnoreCase))
                         ?? outputs.FirstOrDefault();
            CmbOutput.SelectedItem = wanted;
            _applying = false;
            ApplyEngineSettings();
            OpenSelectedOutput();
            UpdateTempoText();
            _loaded = true;
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e) => CancelActiveRun();

        private void SaveSettings()
        {
            if (!_loaded) return;
            _settings.Metronome = ChkClick.IsChecked == true;
            _settings.ClickVolume = (int)SliderClick.Value;
            _settings.CountInBars = Math.Max(0, CmbCountIn.SelectedIndex);
            _settings.TempoPercent = (int)SliderTempo.Value;
            _settings.Loop = ChkLoop.IsChecked == true;
            if (CmbOutput.SelectedItem is MidiDeviceInfo d) _settings.OutputDevice = d.Name;
            JsonSettings.Save(MidiPlayAlongManager.SettingsName, _settings);
        }

        private void QueueSave()
        {
            if (!_loaded || _applying) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void OpenSelectedOutput()
        {
            string? error = _manager.OpenOutput(CmbOutput.SelectedItem as MidiDeviceInfo);
            TxtOutputHint.Text = CmbOutput.SelectedItem == null
                ? Loc.T("Str_PlayAlong_NoOutput", "No MIDI output was found on this PC.")
                : error ?? "";
        }

        private void CmbOutput_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applying || !_loaded) return;
            bool wasPlaying = _manager.IsPlaying;
            double tick = _manager.PositionTick;
            _manager.Stop();
            OpenSelectedOutput();
            if (wasPlaying && _song != null) StartPlayback(tick, countIn: false);
            else ShowPlayState(false);
            QueueSave();
        }

        private void ApplyEngineSettings()
        {
            _manager.ClickOn = ChkClick.IsChecked == true;
            _manager.ClickVolume = (int)SliderClick.Value;
            _manager.CountInBars = Math.Max(0, CmbCountIn.SelectedIndex);
            _manager.TempoScale = SliderTempo.Value / 100.0;
            _manager.Loop = ChkLoop.IsChecked == true;
            ApplyLoopRange();
        }

        private void Click_Changed(object sender, RoutedEventArgs e) { _manager.ClickOn = ChkClick.IsChecked == true; QueueSave(); }
        private void ClickVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_loaded) { _manager.ClickVolume = (int)SliderClick.Value; QueueSave(); } }
        private void CountIn_Changed(object sender, SelectionChangedEventArgs e) { if (_loaded) { _manager.CountInBars = Math.Max(0, CmbCountIn.SelectedIndex); QueueSave(); } }
        private void Loop_Changed(object sender, RoutedEventArgs e) { _manager.Loop = ChkLoop.IsChecked == true; ApplyLoopRange(); QueueSave(); }
        private void LoopBars_Changed(object sender, TextChangedEventArgs e) { if (_loaded) ApplyLoopRange(); }

        private void ApplyLoopRange()
        {
            if (_song == null) { _manager.SetLoopRange(-1, -1); return; }
            int bars = MidiPlayAlongManager.BarCount(_song);
            bool hasFrom = int.TryParse(TxtFromBar.Text.Trim(), out int from);
            bool hasTo = int.TryParse(TxtToBar.Text.Trim(), out int to);
            from = hasFrom ? Math.Clamp(from, 1, bars) : 1;
            to = hasTo ? Math.Clamp(to, from, bars) : bars;
            long start = MidiPlayAlongManager.BarStartTick(_song, from);
            long end = to >= bars ? _song.LengthTicks : MidiPlayAlongManager.BarStartTick(_song, to + 1);
            _manager.SetLoopRange(start, end);
        }

        private void Tempo_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_loaded && !_applying) return;
            _manager.TempoScale = SliderTempo.Value / 100.0;
            UpdateTempoText();
            QueueSave();
        }

        private void BtnTempoReset_Click(object sender, RoutedEventArgs e) => SliderTempo.Value = 100;

        private void UpdateTempoText()
        {
            TxtTempo.Text = $"{(int)SliderTempo.Value} %";
            if (_song != null)
            {
                int bpm = (int)Math.Round(_song.FirstBpm * SliderTempo.Value / 100.0);
                TxtBpm.Text = Loc.F("Str_PlayAlong_BpmHint", "About {0} beats per minute (the song's own is {1}).", bpm, _song.FirstBpm);
                TxtBpmPill.Text = $"♩ {bpm}";
            }
            else { TxtBpm.Text = ""; TxtBpmPill.Text = ""; }
        }

        // =================================================================
        //  Songs (the input card)
        // =================================================================

        private static bool IsMidi(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            var found = new List<string>();
            foreach (string path in e.Paths)
            {
                try
                {
                    if (File.Exists(path)) { if (IsMidi(path)) found.Add(path); }
                    else if (Directory.Exists(path))
                    {
                        var here = Directory.EnumerateFiles(path).Where(IsMidi).ToList();
                        here.Sort(VideoReplayManager.NaturalComparer.Instance);
                        found.AddRange(here);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (found.Count == 0) { TxtStatus.Text = Loc.T("Str_MidiInstruments_MsgNotMidi", "That is not a MIDI file (.mid)."); return; }

            var known = new HashSet<string>(_files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var added = found.Where(known.Add).Select(f => new MidiPlayAlongFile { Path = f }).ToList();
            _files.AddRange(added);
            InputCard.SetEmpty(_files.Count == 0);
            var target = found.Count == 1 ? _files.First(f => string.Equals(f.Path, found[0], StringComparison.OrdinalIgnoreCase)) : _song == null ? added.FirstOrDefault() : null;
            if (target != null) _ = OpenSongAsync(target);
            else TxtStatus.Text = Loc.N(added.Count, "Str_MidiInstruments_MsgFilesOne", "{0} file added. Click one to play it.", "Str_MidiInstruments_MsgFilesMany", "{0} files added. Click one to play it.", added.Count);
        }

        private void SongRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: MidiPlayAlongFile file }) _ = OpenSongAsync(file);
        }

        private void BtnRemoveSong_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.Tag is not MidiPlayAlongFile file) return;
            bool wasCurrent = file.IsCurrent;
            _files.Remove(file);
            InputCard.SetEmpty(_files.Count == 0);
            if (wasCurrent) CloseSong();
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            _files.Clear();
            InputCard.SetEmpty(true);
            CloseSong();
        }

        private void CloseSong()
        {
            _fileToken++;
            _manager.Stop();
            _timer.Stop();
            _song = null;
            _pausedTick = 0;
            TrackList.ItemsSource = null;
            TrackTools.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            SliderPosition.IsEnabled = false;
            BtnPlay.IsEnabled = BtnStop.IsEnabled = false;
            TxtTime.Text = "0:00 / 0:00";
            TxtSongPill.Text = TxtBarPill.Text = "";
            UpdateTempoText();
            ShowPlayState(false);
            ApplyLoopRange();
        }

        private async Task OpenSongAsync(MidiPlayAlongFile file)
        {
            int token = ++_fileToken;
            _manager.Stop();
            ShowPlayState(false);
            TxtStatus.Text = Loc.T("Str_MidiInstruments_MsgReadingFile", "Reading the file…");
            MidiSong song;
            try
            {
                song = await Task.Run(() => MidiPlayAlongManager.ReadSong(file.Path));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                if (token == _fileToken && !_closed) TxtStatus.Text = Loc.F("Str_MidiInstruments_MsgCantRead", "Couldn't open {0}: {1}", file.Name, ex.Message);
                return;
            }
            if (token != _fileToken || _closed) return;

            foreach (var f in _files) f.IsCurrent = ReferenceEquals(f, file);
            _song = song;
            _pausedTick = 0;
            TrackList.ItemsSource = song.Tracks;
            TrackTools.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
            SliderPosition.Maximum = Math.Max(1, song.LengthTicks);
            SliderPosition.Value = 0;
            SliderPosition.IsEnabled = true;
            BtnPlay.IsEnabled = BtnStop.IsEnabled = true;
            TxtSongPill.Text = song.Name;
            TxtStatus.Text = Loc.N(song.Tracks.Count, "Str_PlayAlong_SongOne", "{0} part · {1} bars", "Str_PlayAlong_SongMany", "{0} parts · {1} bars", song.Tracks.Count, MidiPlayAlongManager.BarCount(song));
            ApplyLoopRange();
            UpdateTempoText();
            UpdateClock(0);
        }

        // =================================================================
        //  Playing
        // =================================================================

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_song == null) return;
            if (_manager.IsPlaying) { Pause(); return; }
            StartPlayback(_pausedTick, countIn: _pausedTick <= 0);
        }

        private void StartPlayback(double tick, bool countIn)
        {
            if (_song == null) return;
            string? error = _manager.Play(_song, tick, countIn);
            if (error != null) { TxtStatus.Text = error; ShowPlayState(false); return; }
            ShowPlayState(true);
            _timer.Start();
        }

        private void Pause()
        {
            _pausedTick = Math.Max(0, _manager.PositionTick);
            _manager.Stop();
            ShowPlayState(false);
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _manager.Stop();
            _pausedTick = 0;
            _timer.Stop();
            ShowPlayState(false);
            SliderPosition.Value = 0;
            UpdateClock(0);
        }

        private void OnFinished()
        {
            if (_closed) return;
            _pausedTick = 0;
            _timer.Stop();
            ShowPlayState(false);
            SliderPosition.Value = 0;
            UpdateClock(0);
        }

        private void ShowPlayState(bool playing)
        {
            IconPlay.Kind = playing ? PackIconKind.Pause : PackIconKind.Play;
            TxtPlay.Text = playing ? Loc.T("Str_PlayAlong_Pause", "Pause") : Loc.T("Str_AudioReplayControl_Play", "Play");
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_manager.IsPlaying) { _timer.Stop(); return; }
            double tick = _manager.PositionTick;
            if (!_scrubbing) SliderPosition.Value = Math.Clamp(tick, 0, SliderPosition.Maximum);
            UpdateClock(tick);
        }

        private void UpdateClock(double tick)
        {
            if (_song == null) return;
            double t = Math.Max(0, tick);
            TxtTime.Text = $"{MidiPlayAlongManager.FormatTime(MidiPlayAlongManager.TickToMs(_song, (long)t))} / {MidiPlayAlongManager.FormatTime(MidiPlayAlongManager.TickToMs(_song, _song.LengthTicks))}";
            if (tick < 0) TxtBarPill.Text = Loc.T("Str_PlayAlong_CountInPill", "Count-in");
            else
            {
                var (bar, beat) = MidiPlayAlongManager.BarAndBeat(_song, (long)t);
                TxtBarPill.Text = Loc.F("Str_PlayAlong_BarBeat", "Bar {0} · beat {1}", bar, beat);
            }
        }

        private void Position_Down(object sender, MouseButtonEventArgs e) => _scrubbing = true;

        private void Position_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_scrubbing) UpdateClock(e.NewValue);
        }

        private void Position_Up(object sender, MouseButtonEventArgs e)
        {
            if (!_scrubbing) return;
            _scrubbing = false;
            if (_song == null) return;
            double tick = SliderPosition.Value;
            if (_manager.IsPlaying) StartPlayback(tick, countIn: false);
            else { _pausedTick = tick; UpdateClock(tick); }
        }

        // =================================================================
        //  Parts: mute and solo
        // =================================================================

        private void Mute_Click(object sender, RoutedEventArgs e) => _manager.RefreshMutes();

        private void BtnMuteAll_Click(object sender, RoutedEventArgs e)
        {
            if (_song == null) return;
            foreach (var t in _song.Tracks) { t.IsSolo = false; t.IsMuted = true; }
            _manager.RefreshMutes();
        }

        private void BtnUnmuteAll_Click(object sender, RoutedEventArgs e)
        {
            if (_song == null) return;
            foreach (var t in _song.Tracks) { t.IsSolo = false; t.IsMuted = false; }
            _manager.RefreshMutes();
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || e.OriginalSource is TextBox or ComboBox or CheckBox || _song == null) return;
            BtnPlay_Click(sender, e);
            e.Handled = true;
        }

        // =================================================================

        public void CancelActiveRun()
        {
            _closed = true;
            _timer.Stop();
            _manager.Stop();
            _manager.OpenOutput(null);
            ShowPlayState(false);
            if (_saveTimer.IsEnabled) _saveTimer.Stop();
            SaveSettings();
            _loaded = false;
        }
    }
}
