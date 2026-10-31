// Uses NAudio 3.x (NuGet "NAudio"; needs .NET 9 or later). NAudio 3 changed
// ISampleProvider.Read to take a Span<float>, and WaveOut's latency is now
// set per buffer with BufferMilliseconds (DesiredLatency was removed).

using NAudio.Wave;
using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IOPath = System.IO.Path;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    public enum SynthWaveform { Sine, Square, Sawtooth, Triangle }

    /// <summary>
    /// One playing note: a phase-accumulating oscillator plus its own ADSR
    /// envelope stage/level, so voices fade independently even while the
    /// user is still holding other keys down.
    /// </summary>
    internal sealed class SynthVoice
    {
        private enum Stage { Attack, Decay, Sustain, Release, Done }

        public int MidiNote { get; }
        public bool IsDone => _stage == Stage.Done;

        private readonly int _sampleRate;
        private double _phase;
        private Stage _stage = Stage.Attack;
        private float _level;

        public SynthVoice(int midiNote, int sampleRate)
        {
            MidiNote = midiNote;
            _sampleRate = sampleRate;
        }

        public void Release() { if (_stage != Stage.Done) _stage = Stage.Release; }

        public void Mix(float[] buffer, int count, SynthEngine engine)
        {
            double freq = 440.0 * Math.Pow(2.0, (MidiNote - 69 + engine.OctaveShift * 12) / 12.0);
            freq *= Math.Pow(2.0, engine.DetuneCents / 1200.0);

            double increment = freq / _sampleRate;
            float attack = (float)engine.Attack, decay = (float)engine.Decay;
            float sustain = (float)engine.Sustain, release = (float)engine.Release;

            for (int i = 0; i < count; i++)
            {
                switch (_stage)
                {
                    case Stage.Attack:
                        _level += 1f / (attack * _sampleRate);
                        if (_level >= 1f) { _level = 1f; _stage = Stage.Decay; }
                        break;
                    case Stage.Decay:
                        _level -= (1f - sustain) / (decay * _sampleRate);
                        if (_level <= sustain) { _level = sustain; _stage = Stage.Sustain; }
                        break;
                    case Stage.Sustain:
                        _level = sustain;
                        break;
                    case Stage.Release:
                        _level -= sustain / (release * _sampleRate) + 0.0001f;
                        if (_level <= 0f) { _level = 0f; _stage = Stage.Done; }
                        break;
                }

                buffer[i] += Oscillate(engine.Waveform) * _level;

                _phase += increment;
                if (_phase >= 1.0) _phase -= 1.0;

                if (_stage == Stage.Done)
                    break;
            }
        }

        private float Oscillate(SynthWaveform waveform) => waveform switch
        {
            SynthWaveform.Sine => (float)Math.Sin(2 * Math.PI * _phase),
            SynthWaveform.Square => _phase < 0.5 ? 1f : -1f,
            SynthWaveform.Sawtooth => (float)(2.0 * _phase - 1.0),
            SynthWaveform.Triangle => (float)(1.0 - 4.0 * Math.Abs(Math.Round(_phase - 0.25) - (_phase - 0.25))),
            _ => 0f
        };
    }

    /// <summary>
    /// The synth's mixer: an ISampleProvider that sums every active voice
    /// each Read() call. Parameters are plain public fields the view
    /// updates directly from slider/combo events - no need for
    /// INotifyPropertyChanged on an audio-thread-read object.
    /// </summary>
    internal sealed class SynthEngine : ISampleProvider
    {
        public WaveFormat WaveFormat { get; }

        public SynthWaveform Waveform = SynthWaveform.Sine;
        public double OctaveShift, DetuneCents;
        public double Attack = 0.02, Decay = 0.15, Sustain = 0.7, Release = 0.3;
        public double MasterVolume = 0.5;
        public bool MonoMode;
        public bool SustainPedalDown;

        private readonly List<SynthVoice> _voices = new();
        private readonly HashSet<int> _heldNotes = new();
        private readonly object _lock = new();

        public SynthEngine(int sampleRate) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public IReadOnlyCollection<int> HeldNotes { get { lock (_lock) return _heldNotes.ToArray(); } }

        public void NoteOn(int midiNote)
        {
            lock (_lock)
            {
                if (MonoMode)
                    _voices.Clear();

                _voices.RemoveAll(v => v.MidiNote == midiNote); // retrigger cleanly
                _voices.Add(new SynthVoice(midiNote, WaveFormat.SampleRate));
                _heldNotes.Add(midiNote);
            }
        }

        public void NoteOff(int midiNote)
        {
            lock (_lock)
            {
                _heldNotes.Remove(midiNote);
                if (SustainPedalDown)
                    return;

                foreach (var v in _voices.Where(v => v.MidiNote == midiNote))
                    v.Release();
            }
        }

        public void ReleaseSustainedNotes()
        {
            lock (_lock)
            {
                foreach (var v in _voices.Where(v => !_heldNotes.Contains(v.MidiNote)))
                    v.Release();
            }
        }

        public void PanicAll()
        {
            lock (_lock)
            {
                _voices.Clear();
                _heldNotes.Clear();
            }
        }

        // Reused between calls: allocating a new array on every audio
        // callback made garbage on the playback thread.
        private float[] _scratch = Array.Empty<float>();

        public int Read(Span<float> buffer)
        {
            int count = buffer.Length;
            buffer.Clear();

            lock (_lock)
            {
                if (_scratch.Length < count) _scratch = new float[count];
                foreach (var voice in _voices)
                {
                    Array.Clear(_scratch, 0, count);
                    voice.Mix(_scratch, count, this);
                    for (int i = 0; i < count; i++)
                        buffer[i] += _scratch[i];
                }

                _voices.RemoveAll(v => v.IsDone);

                float gain = (float)(MasterVolume * 0.35); // headroom so stacked notes don't clip
                for (int i = 0; i < count; i++)
                    buffer[i] = Math.Clamp(buffer[i] * gain, -1f, 1f);
            }

            return count;
        }
    }

    /// <summary>
    /// Wraps the engine's output and, while armed, also writes every
    /// sample to a WaveFileWriter - lets "Record Output" capture exactly
    /// what's audibly playing without a second parallel audio path.
    /// </summary>
    internal sealed class RecordingTap : ISampleProvider
    {
        private readonly ISampleProvider _source;
        public WaveFormat WaveFormat => _source.WaveFormat;
        public WaveFileWriter Writer;

        public RecordingTap(ISampleProvider source) => _source = source;

        public int Read(Span<float> buffer)
        {
            int read = _source.Read(buffer);
            // The writer was made with the engine's IEEE-float format, so the
            // samples' bytes are written as they are.
            Writer?.Write(MemoryMarshal.AsBytes(buffer[..read]));
            return read;
        }
    }

    /// <summary>
    /// Interaction logic for SynthesizerView.xaml
    ///
    /// Sidebar sets oscillator/envelope/playback parameters directly on a
    /// live SynthEngine; the main area is an on-screen piano drawn on a
    /// Canvas plus a computer-keyboard mapping, both driving the same
    /// NoteOn/NoteOff calls. Mirrors the app's header/sidebar/card shape;
    /// the header's status strip doubles as an ADSR-aware "what's playing"
    /// readout instead of a progress bar.
    /// </summary>
    public partial class SynthesizerView : UserControl
    {
        private const int LowestNote = 48;  // C3
        private const int HighestNote = 84; // C6 (inclusive)
        private const int KeyboardOctave = 3; // computer keys start at C3 (the lowest key), same layout as MIDI Instruments

        private SynthEngine _engine;
        private RecordingTap _recordingTap;
        private WaveOutEvent _output;

        private readonly Dictionary<int, Border> _keyVisuals = new();
        private readonly Dictionary<int, Rect> _keyRects = new();
        private readonly HashSet<Key> _physicallyHeld = new();
        private int? _mouseNote;
        private bool _spaceHeld;
        private bool _viewConstructed;
        private readonly DispatcherTimer _recTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private DateTime _recStarted;

        public SynthesizerView()
        {
            InitializeComponent();
            _recTimer.Tick += (_, _) => TxtRecorderTime.Text = FormatElapsed(DateTime.UtcNow - _recStarted);
            _viewConstructed = true;
        }

        private static string FormatElapsed(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            _engine = new SynthEngine(44100);
            _recordingTap = new RecordingTap(_engine);

            _output = new WaveOutEvent { BufferMilliseconds = 30 };   // NAudio 3: per buffer (2 x 30 ms ≈ the old 60 ms total)
            _output.Init(_recordingTap.ToWaveProvider());
            _output.Play();

            UpdateEngineFromSliders();
            DrawKeyboard();
            Root.Focus();
        }

        /// <summary>Window closing: release the audio device and discard any scratch recording (safe to call twice).</summary>
        public void CancelActiveRun() => Root_Unloaded(this, new RoutedEventArgs());

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            _output?.Stop();
            _output?.Dispose();
            _output = null;
            StopRecording();

            // Leaving the view mid-recording: there's no one left to answer
            // a Save dialog, so just discard the scratch file rather than
            // leaving it orphaned in the temp folder.
            if (_tempRecordingPath != null)
            {
                try { File.Delete(_tempRecordingPath); }
                catch { /* best-effort */ }
                _tempRecordingPath = null;
            }
            _isRecording = false;
            _recTimer.Stop();
        }

        // ---------------- Parameter wiring ----------------

        private void WaveformCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_engine == null || WaveformCombo.SelectedItem is not ComboBoxItem item)
                return;

            _engine.Waveform = Enum.TryParse((string)item.Tag, out SynthWaveform wf) ? wf : SynthWaveform.Sine;
            TxtWaveName.Text = item.Content as string ?? item.Tag.ToString();
            IconWave.Kind = _engine.Waveform switch
            {
                SynthWaveform.Square => PackIconKind.SquareWave,
                SynthWaveform.Sawtooth => PackIconKind.SawtoothWave,
                SynthWaveform.Triangle => PackIconKind.TriangleWave,
                _ => PackIconKind.SineWave,
            };
        }

        // Sliders bind their displayed value via {Binding ElementName=...} in
        // XAML; the engine's plain fields still need pushing on ValueChanged
        // since SynthEngine isn't a DependencyObject/INotifyPropertyChanged
        // type (it's read from the audio thread and kept as simple as
        // possible on purpose).
        private void UpdateEngineFromSliders()
        {
            if (_engine == null) return;

            _engine.OctaveShift = OctaveSlider.Value;
            _engine.DetuneCents = DetuneSlider.Value;
            _engine.Attack = AttackSlider.Value;
            _engine.Decay = DecaySlider.Value;
            _engine.Sustain = SustainSlider.Value;
            _engine.Release = ReleaseSlider.Value;
            _engine.MasterVolume = VolumeSlider.Value;
            _engine.MonoMode = RadioMono.IsChecked == true;
            _engine.SustainPedalDown = SustainPedalCheckBox.IsChecked == true || _spaceHeld;

            TxtVoiceMode.Text = (string)FindResource(_engine.MonoMode ? "Str_Synth_ModeMono" : "Str_Synth_ModePoly");
            SustainPill.Visibility = _engine.SustainPedalDown ? Visibility.Visible : Visibility.Collapsed;
        }

        // Sliders and the mode/pedal toggles all push live into the engine
        // on every change - a synth's controls are meant to be heard while
        // a note rings, not just applied to the next note played.
        private void ParamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            UpdateEngineFromSliders();

        private void ParamCheck_Changed(object sender, RoutedEventArgs e) => UpdateEngineFromSliders();

        private void SustainPedalCheckBox_Checked(object sender, RoutedEventArgs e) => UpdateEngineFromSliders();

        private void SustainPedalCheckBox_Unchecked(object sender, RoutedEventArgs e) => ReleasePedalIfOff();

        /// <summary>Lets go of anything only being held by the pedal, once neither the checkbox nor Space is holding it.</summary>
        private void ReleasePedalIfOff()
        {
            UpdateEngineFromSliders();
            if (_engine is { SustainPedalDown: false })
                _engine.ReleaseSustainedNotes();
        }

        // ---------------- Recording ----------------
        //
        // One toggle button instead of a checkbox + separate "Save" button:
        // click once to start (recording begins immediately into a scratch
        // temp file - no dialog in the way), click again to stop, and THEN
        // the Save dialog appears so you pick where the finished take goes.
        //
        // This also removes the likely cause of the red indicator seeming
        // to only "come alive" alongside the sustain pedal: the old
        // RecordOutputCheckBox_Checked handler set RecDot.Opacity = 1 only
        // AFTER a modal SaveFileDialog.ShowDialog() call returned, so the
        // checkbox itself flipped to checked well before the dot caught up -
        // any later redraw (like toggling the pedal, which repaints the
        // sidebar) could look like what finally made the dot "work". With
        // no dialog sitting between the click and the opacity change, the
        // dot now updates in the same synchronous handler as the button
        // click, every time.

        private bool _isRecording;
        private string _tempRecordingPath;

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecording)
                StopAndSaveRecording();
            else
                StartRecording();
        }

        private void StartRecording()
        {
            if (_engine == null || _recordingTap == null)
                return;

            string tempPath = IOPath.Combine(IOPath.GetTempPath(), $"SmartTools-Synth-{Guid.NewGuid():N}.wav");

            try
            {
                _recordingTap.Writer = new WaveFileWriter(tempPath, _engine.WaveFormat);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't start recording: {ex.Message}", "Synthesizer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _tempRecordingPath = tempPath;
            _isRecording = true;
            RecDot.Opacity = 1;
            _recStarted = DateTime.UtcNow;
            TxtRecorderTime.Text = "0:00";
            _recTimer.Start();
            SetRecordButtonState(recording: true);
            StatusText.Text = (string)FindResource("Str_Synth_RecordingStatus");
        }

        private void StopAndSaveRecording()
        {
            StopRecording();
            _isRecording = false;
            _recTimer.Stop();
            RecDot.Opacity = 0;
            SetRecordButtonState(recording: false);
            StatusText.Text = (string)FindResource("Str_Synth_ReadyStatus");

            string finishedPath = _tempRecordingPath;
            _tempRecordingPath = null;

            if (finishedPath == null || !File.Exists(finishedPath))
                return;

            var dialog = new SaveFileDialog
            {
                Title = "Save recording",
                Filter = "WAV audio|*.wav",
                DefaultExt = ".wav",
                FileName = $"Synth {DateTime.Now:yyyy-MM-dd HH-mm-ss}.wav"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    File.Copy(finishedPath, dialog.FileName, overwrite: true);
                    StatusText.Text = string.Format((string)FindResource("Str_Synth_SavedStatus"), dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Couldn't save: {ex.Message}", "Synthesizer",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            try { File.Delete(finishedPath); }
            catch { /* best-effort scratch-file cleanup */ }
        }

        private void StopRecording()
        {
            if (_recordingTap == null) return;
            _recordingTap.Writer?.Dispose();
            _recordingTap.Writer = null;
        }

        /// <summary>Swaps the button icon, label and colour between idle and recording.</summary>
        private void SetRecordButtonState(bool recording)
        {
            BtnRecord.Style = (Style)FindResource(recording ? "DangerButtonStyle" : "AccentRedButtonStyle");
            IconRecord.Kind = recording ? PackIconKind.Stop : PackIconKind.RecordCircleOutline;
            TxtRecordLabel.Text = (string)FindResource(recording ? "Str_Synth_StopRecording" : "Str_MicRecorder_Record");
        }

        // ---------------- Panic ----------------

        private void BtnPanic_Click(object sender, RoutedEventArgs e)
        {
            _engine?.PanicAll();
            _physicallyHeld.Clear();
            _spaceHeld = false;
            UpdateEngineFromSliders();
            foreach (var rect in _keyVisuals.Values)
                SetKeyPressedVisual(rect, false);
            RefreshActiveNotesText();
        }

        // ---------------- Note on/off (shared by mouse + keyboard) ----------------

        private void PlayNote(int midiNote)
        {
            if (_engine == null || midiNote < 0 || midiNote > 127)
                return;

            UpdateEngineFromSliders();
            _engine.NoteOn(midiNote);
            RefreshActiveNotesText();

            TxtLastNote.Text = NoteName(midiNote);
            UiMotion.Pop(TxtLastNote, 0.9);

            if (_keyVisuals.TryGetValue(midiNote, out var rect))
                SetKeyPressedVisual(rect, true);
        }

        private void ReleaseNote(int midiNote)
        {
            _engine?.NoteOff(midiNote);
            RefreshActiveNotesText();

            if (_keyVisuals.TryGetValue(midiNote, out var rect))
                SetKeyPressedVisual(rect, false);
        }

        private void RefreshActiveNotesText()
        {
            var held = _engine?.HeldNotes.OrderBy(n => n).ToArray() ?? Array.Empty<int>();
            TxtActiveNotes.Text = held.Length == 0
                ? (string)FindResource("Str_Synth_NoActiveNotes")
                : string.Format((string)FindResource("Str_Synth_ActiveNotes"), string.Join(" ", held.Select(NoteName)));
        }

        private static string NoteName(int midiNote) => MidiInstrumentsManager.NoteName(midiNote);

        // ---------------- Computer keyboard input ----------------

        /// <summary>Letters play (same layout as MIDI Instruments), Space holds sustain, Esc silences everything.</summary>
        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            switch (key)
            {
                case Key.Space:
                    if (!e.IsRepeat)
                    {
                        _spaceHeld = true;
                        UpdateEngineFromSliders();
                    }
                    e.Handled = true;   // also stops Space from pressing a focused button
                    return;
                case Key.Escape:
                    BtnPanic_Click(this, e);
                    e.Handled = true;
                    return;
            }

            if (e.IsRepeat || MidiInstrumentsManager.NoteForComputerKey(key.ToString(), KeyboardOctave) is not int note || !_physicallyHeld.Add(key))
                return;

            PlayNote(note);
            e.Handled = true;
        }

        private void Root_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Space)
            {
                _spaceHeld = false;
                ReleasePedalIfOff();
                e.Handled = true;
                return;
            }

            if (MidiInstrumentsManager.NoteForComputerKey(key.ToString(), KeyboardOctave) is not int note || !_physicallyHeld.Remove(key))
                return;

            ReleaseNote(note);
            e.Handled = true;
        }

        private void KeyLabels_Changed(object sender, RoutedEventArgs e)
        {
            if (_viewConstructed) DrawKeyboard();
        }

        // ---------------- On-screen piano ----------------
        //
        // Styled and driven like MIDI Instruments' keyboard: Borders (so only
        // the bottom corners round, like a real key) on a Canvas, hit-tested
        // by rectangle rather than per-element mouse events, with the mouse
        // captured by the Canvas itself. That's what lets a press-and-drag
        // glissando work - with capture on an individual key, every later
        // MouseMove still routes to that key, so other keys never see the
        // pointer arrive; capturing the Canvas instead lets one drag walk
        // across every key underneath it.

        private void PianoCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawKeyboard();

        private void DrawKeyboard()
        {
            PianoCanvas.Children.Clear();
            _keyVisuals.Clear();
            _keyRects.Clear();

            double width = PianoCanvas.ActualWidth;
            double height = PianoCanvas.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            var whiteNotes = Enumerable.Range(LowestNote, HighestNote - LowestNote + 1)
                .Where(n => !IsSharp(n)).ToList();

            double whiteKeyWidth = width / whiteNotes.Count;

            // White keys first (bottom layer)
            for (int i = 0; i < whiteNotes.Count; i++)
            {
                int note = whiteNotes[i];
                var rect = new Rect(i * whiteKeyWidth, 0, whiteKeyWidth, height);
                var key = MakeKeyVisual(note, sharp: false);
                Canvas.SetLeft(key, rect.X + 1);
                Canvas.SetTop(key, 0);
                key.Width = Math.Max(1, rect.Width - 2);
                key.Height = rect.Height;
                Canvas.SetZIndex(key, 0);
                PianoCanvas.Children.Add(key);
                _keyVisuals[note] = key;
                _keyRects[note] = rect;
            }

            // Black keys on top, positioned relative to the white key they sit between
            double blackKeyWidth = whiteKeyWidth * 0.6;
            double blackKeyHeight = height * 0.6;
            int whiteIndex = 0;
            for (int note = LowestNote; note <= HighestNote; note++)
            {
                if (!IsSharp(note)) { whiteIndex++; continue; }

                double centerX = whiteIndex * whiteKeyWidth; // boundary just left of the upcoming white key
                var rect = new Rect(centerX - blackKeyWidth / 2, 0, blackKeyWidth, blackKeyHeight);
                var key = MakeKeyVisual(note, sharp: true);
                Canvas.SetLeft(key, rect.X);
                Canvas.SetTop(key, 0);
                key.Width = rect.Width;
                key.Height = rect.Height;
                Canvas.SetZIndex(key, 1);
                PianoCanvas.Children.Add(key);
                _keyVisuals[note] = key;
                _keyRects[note] = rect;
            }

            foreach (int held in _engine?.HeldNotes ?? Array.Empty<int>())
                if (_keyVisuals.TryGetValue(held, out var heldKey)) SetKeyPressedVisual(heldKey, true);
        }

        private static bool IsSharp(int midiNote) => MidiInstrumentsManager.IsBlackKey(midiNote);

        private Border MakeKeyVisual(int note, bool sharp)
        {
            var labels = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, sharp ? 6 : 8),
                IsHitTestVisible = false,
            };

            string letter = MidiInstrumentsManager.ComputerKeyLabel(note, KeyboardOctave);
            if (ChkShowComputerKeys.IsChecked == true && letter.Length > 0)
            {
                var chip = new Border
                {
                    Padding = new Thickness(4, 0, 4, 1),
                    Margin = new Thickness(0, 0, 0, 3),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = letter, FontSize = 10, FontWeight = FontWeights.SemiBold },
                };
                chip.SetResourceReference(Border.BorderBrushProperty, sharp ? "PianoBlackKeyText" : "PianoWhiteKeyEdge");
                ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, sharp ? "PianoBlackKeyText" : "PianoWhiteKeyText");
                labels.Children.Add(chip);
            }
            if (ChkShowNoteNames.IsChecked == true && !sharp)
            {
                var name = new TextBlock
                {
                    Text = NoteName(note),
                    FontSize = 10.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontWeight = note == 60 ? FontWeights.Bold : FontWeights.Normal,   // middle C
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, note % 12 == 0 ? "AccentColor" : "PianoWhiteKeyText");
                labels.Children.Add(name);
            }

            var key = new Border
            {
                Tag = note,
                BorderThickness = new Thickness(1),
                CornerRadius = sharp ? new CornerRadius(0, 0, 5, 5) : new CornerRadius(0, 0, 7, 7),
                Child = labels,
                RenderTransform = new TranslateTransform(),
                ToolTip = NoteName(note),
            };
            key.SetResourceReference(Border.BackgroundProperty, sharp ? "PianoBlackKey" : "PianoWhiteKey");
            key.SetResourceReference(Border.BorderBrushProperty, sharp ? "PianoBlackKeyEdge" : "PianoWhiteKeyEdge");
            return key;
        }

        /// <summary>Black keys are checked first - they sit on top of the whites.</summary>
        private int? KeyAt(Point p)
        {
            foreach (var (note, rect) in _keyRects)
                if (IsSharp(note) && rect.Contains(p)) return note;
            foreach (var (note, rect) in _keyRects)
                if (!IsSharp(note) && rect.Contains(p)) return note;
            return null;
        }

        private void SetKeyPressedVisual(Border key, bool pressed)
        {
            bool sharp = key.Tag is int note && IsSharp(note);
            key.SetResourceReference(Border.BackgroundProperty, pressed ? "AccentColor" : sharp ? "PianoBlackKey" : "PianoWhiteKey");
            key.SetResourceReference(Border.BorderBrushProperty, pressed ? "AccentColorPressed" : sharp ? "PianoBlackKeyEdge" : "PianoWhiteKeyEdge");
            if (key.RenderTransform is TranslateTransform shift) shift.Y = pressed ? 2 : 0; // the key dips a little
        }

        private void PianoCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Focus(); // so the computer keys play straight after
            var p = e.GetPosition(PianoCanvas);
            if (KeyAt(p) is not int note) return;
            PianoCanvas.CaptureMouse();
            _mouseNote = note;
            PlayNote(note);
            e.Handled = true;
        }

        /// <summary>Dragging across the keys plays each one in turn (a glissando).</summary>
        private void PianoCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_mouseNote is not int current || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(PianoCanvas);
            int? note = KeyAt(p);
            if (note == current) return;
            ReleaseNote(current);
            _mouseNote = note;
            if (note is int n) PlayNote(n);
        }

        private void PianoCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_mouseNote is int note) ReleaseNote(note);
            _mouseNote = null;
            if (PianoCanvas.IsMouseCaptured) PianoCanvas.ReleaseMouseCapture();
        }

        private void PianoCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_mouseNote is int note) ReleaseNote(note);
            _mouseNote = null;
        }
    }
}
