using MaterialDesignThemes.Wpf;
using NAudio.Wave;
using Smart_Tools.WPF.Controllers;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Metronome - the screen and the sound device. One bar of clicks (<see cref="MetronomeManager.BuildBar"/>) is played
    /// in a loop, so the tempo can't drift; the lit beat is read from the playback position, so it matches what you hear.
    /// </summary>
    public partial class MetronomeView : UserControl
    {
        /// <summary>Plays a block of 16-bit mono PCM over and over.</summary>
        private sealed class LoopingPcm : IWaveProvider
        {
            private readonly byte[] _data;
            private int _position;
            public LoopingPcm(byte[] data) { _data = data; WaveFormat = new WaveFormat(MetronomeManager.SampleRate, 16, 1); }
            public WaveFormat WaveFormat { get; }

            public int Read(Span<byte> buffer)
            {
                int written = 0;
                while (written < buffer.Length)
                {
                    int chunk = Math.Min(buffer.Length - written, _data.Length - _position);
                    _data.AsSpan(_position, chunk).CopyTo(buffer.Slice(written));
                    written += chunk;
                    _position = (_position + chunk) % _data.Length;
                }
                return buffer.Length;
            }
        }

        public sealed record BeatDot(System.Windows.Media.Brush Fill);

        private readonly DispatcherTimer _beatTimer;
        private readonly DispatcherTimer _restartTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly MetronomeManager.TapTempo _tap = new();
        private readonly bool _viewConstructed;
        private bool _loadedOnce;
        private bool _applying;
        private WaveOutEvent? _output;
        private int _bpm = 100;
        private int _lastBeat = -1;

        public MetronomeView()
        {
            InitializeComponent();
            foreach (int n in MetronomeManager.BeatChoices) CmbBeats.Items.Add(new ComboBoxItem { Content = n == 1 ? "1 beat" : n + " beats", Tag = n });
            _beatTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
            _beatTimer.Tick += (_, _) => UpdateBeat();
            _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _restartTimer.Tick += (_, _) => { _restartTimer.Stop(); if (IsPlaying) StartSound(); };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _viewConstructed = true;
        }

        private bool IsPlaying => _output != null;
        private int Beats => (CmbBeats.SelectedItem as ComboBoxItem)?.Tag is int n ? n : 4;

        // =================================================================
        //  Settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            var s = MetronomeManager.Clamp(JsonSettings.Load<MetronomeSettings>(MetronomeManager.SettingsName));
            _applying = true;
            _bpm = s.Bpm;
            SliderBpm.Value = s.Bpm;
            SliderVolume.Value = s.Volume;
            ChkAccent.IsChecked = s.AccentFirst;
            foreach (ComboBoxItem item in CmbBeats.Items) if ((int)item.Tag == s.BeatsPerBar) CmbBeats.SelectedItem = item;
            _applying = false;
            ShowBpm();
            BuildDots(-1);
            Focus();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
            Stop();
        }

        private void SaveSettings() => JsonSettings.Save(MetronomeManager.SettingsName, MetronomeManager.Clamp(new MetronomeSettings
        {
            Bpm = _bpm,
            BeatsPerBar = Beats,
            AccentFirst = ChkAccent.IsChecked == true,
            Volume = (int)SliderVolume.Value,
        }));

        private void QueueSave()
        {
            if (!_viewConstructed || _applying) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // =================================================================
        //  Tempo
        // =================================================================

        private void SetBpm(int bpm)
        {
            bpm = Math.Clamp(bpm, MetronomeManager.MinBpm, MetronomeManager.MaxBpm);
            if (bpm == _bpm && Math.Abs(SliderBpm.Value - bpm) < 0.5) return;
            _bpm = bpm;
            _applying = true;
            SliderBpm.Value = bpm;
            _applying = false;
            ShowBpm();
            Changed();
        }

        private void ShowBpm()
        {
            TxtBpm.Text = _bpm.ToString(CultureInfo.InvariantCulture);
            TxtTempoName.Text = MetronomeManager.TempoName(_bpm);
        }

        private void Bpm_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || _applying) return;
            _bpm = (int)Math.Round(e.NewValue);
            ShowBpm();
            Changed();
        }

        private void BtnMinus_Click(object sender, RoutedEventArgs e) => SetBpm(_bpm - (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1));
        private void BtnPlus_Click(object sender, RoutedEventArgs e) => SetBpm(_bpm + (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1));

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string t && int.TryParse(t, out int bpm)) SetBpm(bpm);
        }

        private void BtnTap_Click(object sender, RoutedEventArgs e)
        {
            int? bpm = _tap.Tap(DateTime.UtcNow.Ticks);
            if (bpm is { } b) SetBpm(b);
        }

        private void Beats_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            BuildDots(-1);
            Changed();
        }

        private void Accent_Click(object sender, RoutedEventArgs e) => Changed();
        private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_viewConstructed && !_applying) Changed(); }

        /// <summary>Settings changed: remember them, and if it is playing, restart with the new bar a moment later.</summary>
        private void Changed()
        {
            QueueSave();
            if (!IsPlaying) return;
            _restartTimer.Stop();
            _restartTimer.Start();
        }

        // =================================================================
        //  Sound
        // =================================================================

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (IsPlaying) Stop();
            else StartSound();
            Focus();
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox or System.Windows.Controls.ComboBox) return;
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1;
            switch (e.Key)
            {
                case Key.Space: BtnStart_Click(sender, e); e.Handled = true; break;
                case Key.Up: SetBpm(_bpm + step); e.Handled = true; break;
                case Key.Down: SetBpm(_bpm - step); e.Handled = true; break;
            }
        }

        private void StartSound()
        {
            StopDevice();
            TxtMessage.Text = "";
            try
            {
                var bar = MetronomeManager.BuildBar(_bpm, Beats, ChkAccent.IsChecked == true, (int)SliderVolume.Value);
                _output = new WaveOutEvent { BufferMilliseconds = 40 };   // NAudio 3: per buffer, there are two
                _output.Init(new LoopingPcm(bar));
                _output.Play();
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                StopDevice();
                TxtMessage.Text = Loc.F("Str_Metronome_MsgStartFailed", "Couldn't start the sound: {0}", ex.Message);
                return;
            }
            _lastBeat = -1;
            _beatTimer.Start();
            TxtStart.Text = Loc.T("Str_Metronome_Stop", "Stop");
            IconStart.Kind = PackIconKind.Stop;
        }

        private void StopDevice()
        {
            _beatTimer.Stop();
            var output = _output;
            _output = null;
            if (output == null) return;
            try { output.Stop(); } catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException) { }
            output.Dispose();
        }

        private void Stop()
        {
            _restartTimer.Stop();
            StopDevice();
            TxtStart.Text = Loc.T("Str_Metronome_Start", "Start");
            IconStart.Kind = PackIconKind.Play;
            BuildDots(-1);
        }

        // =================================================================
        //  The lit beat
        // =================================================================

        private void UpdateBeat()
        {
            if (_output == null) return;
            long position;
            try { position = _output.GetPosition(); }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException) { return; }
            int beat = MetronomeManager.BeatAt(position, _bpm, Beats);
            if (beat == _lastBeat) return;
            _lastBeat = beat;
            BuildDots(beat);
        }

        private void BuildDots(int active)
        {
            var on = (System.Windows.Media.Brush)FindResource("AccentColor") is SolidColorBrush accent ? accent : System.Windows.Media.Brushes.DodgerBlue;
            var first = System.Windows.Media.Brushes.OrangeRed;
            var off = (System.Windows.Media.Brush)FindResource("InputBg");
            var dots = new List<BeatDot>();
            for (int i = 0; i < Beats; i++)
                dots.Add(new BeatDot(i == active ? (i == 0 && ChkAccent.IsChecked == true ? first : on) : off));
            BeatDots.ItemsSource = dots;
        }

        public void CancelActiveRun()
        {
            Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
