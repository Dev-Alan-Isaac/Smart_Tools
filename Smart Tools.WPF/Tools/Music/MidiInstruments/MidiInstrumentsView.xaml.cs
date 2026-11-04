using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Engines.Midi;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// MIDI Instruments - UI only. Playing, sustain, the MIDI keyboard,
    /// recording and playback are in <see cref="MidiInstrumentsManager"/>;
    /// the device calls in Engines/Midi. This file draws the piano, turns
    /// clicks and key presses into notes, and shows what's happening.
    /// </summary>
    public partial class MidiInstrumentsView : UserControl
    {
        private static string DialogTitle => Loc.T("Str_Tool_midi-instruments_Name", "MIDI Instruments");
        private static readonly MidiDeviceInfo NoInputItem = new(-1, "None");

        private readonly MidiInstrumentsManager _manager = new();
        private readonly DispatcherTimer _recorderTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly bool _viewConstructed;

        private MidiInstrumentsSettings _settings = new();
        private List<MidiPianoKey> _keys = new();
        private readonly Dictionary<int, Border> _keyViews = new();
        private readonly Dictionary<int, Rect> _keyRects = new();
        private readonly HashSet<int> _lit = new();
        private int _octave = 4;
        private int? _mouseNote;
        private bool _loadedOnce;
        private bool _applying;
        private bool _closed;
        private Window? _window;

        private MidiRecording? _take;
        private bool _takeSaved = true;
        private string? _lastSaveFolder;   // this session only - never saved
        private int _pulse;

        public MidiInstrumentsView()
        {
            InitializeComponent();

            CmbFamily.ItemsSource = MidiInstrumentCatalog.Families;

            // The manager raises these on whatever thread played the note (the
            // MIDI keyboard has its own); the screen is only touched on ours.
            _manager.KeyStateChanged += (note, down) => Dispatcher.BeginInvoke(() => SetKeyLit(note, down));
            _manager.NotePlayed += (note, velocity, source) => Dispatcher.BeginInvoke(() => ShowLastNote(note, velocity, source));
            _manager.InstrumentChangedFromDevice += instrument => Dispatcher.BeginInvoke(() => ShowInstrument(instrument, syncList: true));

            _recorderTimer.Tick += RecorderTimer_Tick;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops playback and recording, silences every note and lets go of the synth and keyboard.</summary>
        public void CancelActiveRun()
        {
            _closed = true;
            _recorderTimer.Stop();
            _manager.Dispose();
        }

        // =================================================================
        //  Lifetime
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                ApplySettings(MidiInstrumentsManager.LoadSettings());
                RefreshDevices();
                _loadedOnce = true;
            }

            _window = Window.GetWindow(this);
            if (_window != null) _window.Deactivated += Window_Deactivated;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Focus());
        }

        /// <summary>Switching to another tool: let go of keys held by mouse or keyboard (their key-up would never arrive here).</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_window != null) _window.Deactivated -= Window_Deactivated;
            _window = null;
            ReleaseLocalKeys();
        }

        /// <summary>Alt+Tab with a key held used to leave the note ringing forever.</summary>
        private void Window_Deactivated(object? sender, EventArgs e) => ReleaseLocalKeys();

        private void ReleaseLocalKeys()
        {
            if (_closed) return;
            _mouseNote = null;
            if (PianoCanvas.IsMouseCaptured) PianoCanvas.ReleaseMouseCapture();
            _manager.ReleaseSource(MidiNoteSource.Mouse);
            _manager.ReleaseSource(MidiNoteSource.ComputerKeyboard);
            UpdateSustainPill();
        }

        // =================================================================
        //  Settings
        // =================================================================

        private void ApplySettings(MidiInstrumentsSettings s)
        {
            _applying = true;
            try
            {
                _settings = s;
                _octave = s.Octave;
                SldVelocity.Value = s.Velocity;
                SldVolume.Value = s.Volume;
                SldReverb.Value = s.Reverb;
                ChkTouch.IsChecked = s.Touch == MidiTouchMode.ByClickHeight;
                ChkShowNoteNames.IsChecked = s.ShowNoteNames;
                ChkShowComputerKeys.IsChecked = s.ShowComputerKeys;
                _manager.Velocity = s.Velocity;
                _manager.Volume = s.Volume;
                _manager.Reverb = s.Reverb;
                UpdateSliderTexts();

                var instrument = MidiInstrumentsManager.FindInstrument(s.Instrument);
                _manager.SetInstrument(instrument);
                CmbFamily.SelectedItem = instrument.Family;
            }
            finally
            {
                _applying = false;
            }
            ShowInstrument(_manager.Instrument, syncList: true);
            UpdateOctaveUi();
        }

        private void SaveSettings()
        {
            if (_applying || !_loadedOnce || _closed) return;
            _settings.Instrument = _manager.Instrument.Key;
            _settings.Octave = _octave;
            _settings.Velocity = (int)SldVelocity.Value;
            _settings.Volume = (int)SldVolume.Value;
            _settings.Reverb = (int)SldReverb.Value;
            _settings.Touch = ChkTouch.IsChecked == true ? MidiTouchMode.ByClickHeight : MidiTouchMode.Fixed;
            _settings.ShowNoteNames = ChkShowNoteNames.IsChecked == true;
            _settings.ShowComputerKeys = ChkShowComputerKeys.IsChecked == true;
            try
            {
                MidiInstrumentsManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"MIDI settings not saved: {ex.Message}");
            }
        }

        // =================================================================
        //  Instruments
        // =================================================================

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            BtnClearSearch.Visibility = string.IsNullOrEmpty(TxtSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
            RefreshInstrumentList();
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Clear();
            TxtSearch.Focus();
        }

        private void CmbFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (CmbFamily.SelectedItem is MidiInstrumentFamily family) IconFamily.Kind = IconFor(family);
            if (!_applying && !string.IsNullOrEmpty(TxtSearch.Text)) TxtSearch.Clear();   // picking a family ends a search
            RefreshInstrumentList();
        }

        /// <summary>The list: the family's instruments, or search results from all 136 (with their family shown).</summary>
        private void RefreshInstrumentList()
        {
            bool searching = !string.IsNullOrWhiteSpace(TxtSearch.Text);
            var list = MidiInstrumentsManager.Filter(TxtSearch.Text, CmbFamily.SelectedItem as MidiInstrumentFamily);
            bool was = _applying;
            _applying = true;
            try
            {
                ListInstruments.ItemsSource = list;
                ListInstruments.Tag = searching ? Visibility.Visible : Visibility.Collapsed;
                ListInstruments.SelectedItem = list.Contains(_manager.Instrument) ? _manager.Instrument : null;
                if (ListInstruments.SelectedItem != null) ListInstruments.ScrollIntoView(ListInstruments.SelectedItem);
            }
            finally
            {
                _applying = was;
            }
            FamilyBar.Opacity = searching ? 0.5 : 1;
            TxtInstrumentHint.Text = !searching
                ? "Click a sound and play - the keys change straight away."
                : list.Count == 0
                    ? "Nothing matches - try \"piano\", \"strings\", \"sax\" or \"drum\"."
                    : $"{list.Count} {(list.Count == 1 ? "sound" : "sounds")} found.";
        }

        private void ListInstruments_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applying || ListInstruments.SelectedItem is not MidiInstrument instrument) return;
            if (instrument == _manager.Instrument) return;
            bool drumsChanged = instrument.IsDrumKit != _manager.Instrument.IsDrumKit;
            _manager.SetInstrument(instrument);
            ShowInstrument(instrument, syncList: false);
            if (drumsChanged) BuildKeyboard();
            SaveSettings();
        }

        /// <summary>Header card for the chosen sound; with syncList the sidebar follows too (a program change from the MIDI keyboard).</summary>
        private void ShowInstrument(MidiInstrument instrument, bool syncList)
        {
            if (_closed) return;
            TxtInstrumentName.Text = instrument.Name;
            TxtFamily.Text = instrument.Family.Name;
            IconInstrument.Kind = IconFamilyPill.Kind = IconFor(instrument.Family);
            TxtChannel.Text = instrument.IsDrumKit
                ? $"Drums · Channel {instrument.Channel + 1}"
                : $"Program {instrument.Program + 1} · Channel {instrument.Channel + 1}";
            UiMotion.Pop(InstrumentBadge, 0.85);

            if (!syncList) return;
            _applying = true;
            try
            {
                if (!string.IsNullOrEmpty(TxtSearch.Text)) TxtSearch.Clear();
                CmbFamily.SelectedItem = instrument.Family;
                IconFamily.Kind = IconFor(instrument.Family);
            }
            finally
            {
                _applying = false;
            }
            RefreshInstrumentList();
            if (_keys.Count > 0 && _keys.Any(k => k.DrumName != null) != instrument.IsDrumKit) BuildKeyboard();
        }

        private static PackIconKind IconFor(MidiInstrumentFamily family) =>
            Enum.TryParse(family.Icon, out PackIconKind kind) ? kind : PackIconKind.MusicNote;

        // =================================================================
        //  Sound
        // =================================================================

        private void SldVelocity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            _manager.Velocity = (int)Math.Round(e.NewValue);
            UpdateSliderTexts();
            SaveSettings();
        }

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            _manager.Volume = (int)Math.Round(e.NewValue);
            UpdateSliderTexts();
            SaveSettings();
        }

        private void SldReverb_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            _manager.Reverb = (int)Math.Round(e.NewValue);
            UpdateSliderTexts();
            SaveSettings();
        }

        private void UpdateSliderTexts()
        {
            TxtVelocityValue.Text = $"{SldVelocity.Value:0}";
            TxtVolumeValue.Text = $"{SldVolume.Value / 127 * 100:0}%";
            TxtReverbValue.Text = $"{SldReverb.Value / 127 * 100:0}%";
        }

        private void ChkTouch_Changed(object sender, RoutedEventArgs e) => SaveSettings();

        private void ChkSustain_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _manager.SustainLatched = ChkSustain.IsChecked == true;
            UpdateSustainPill();
        }

        private void UpdateSustainPill()
        {
            bool on = _manager.SustainOn;
            if (on == (SustainPill.Visibility == Visibility.Visible)) return;
            SustainPill.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) UiMotion.Pop(SustainPill, 0.8);
        }

        // =================================================================
        //  Octave and key labels
        // =================================================================

        private void BtnOctaveDown_Click(object sender, RoutedEventArgs e) => SetOctave(_octave - 1);

        private void BtnOctaveUp_Click(object sender, RoutedEventArgs e) => SetOctave(_octave + 1);

        /// <summary>Keys already held keep sounding and stop correctly - the manager remembers which note each key started.</summary>
        private void SetOctave(int octave)
        {
            octave = Math.Clamp(octave, MidiInstrumentsManager.MinOctave, MidiInstrumentsManager.MaxOctave);
            if (octave == _octave) return;
            _octave = octave;
            UpdateOctaveUi();
            UiMotion.Pop(OctavePill, 0.9);
            SaveSettings();
        }

        private void UpdateOctaveUi()
        {
            TxtOctave.Text = $"Octave {_octave}";
            int first = MidiInstrumentsManager.OctaveStart(_octave);
            int last = Math.Min(127, first + MidiInstrumentsManager.VisibleOctaves * 12);
            string range = $"{MidiInstrumentsManager.NoteName(first)} – {MidiInstrumentsManager.NoteName(last)}";
            TxtOctaveRange.Text = range;
            TxtKeyboardRange.Text = range + (_octave == 4 ? " · middle C on the left" : "");
            BtnOctaveDown.IsEnabled = _octave > MidiInstrumentsManager.MinOctave;
            BtnOctaveUp.IsEnabled = _octave < MidiInstrumentsManager.MaxOctave;
            BuildKeyboard();
        }

        private void KeyLabels_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            BuildKeyboard();
            SaveSettings();
        }

        // =================================================================
        //  The piano
        // =================================================================

        /// <summary>Rebuilds the key elements (whites first so the blacks sit on top).</summary>
        private void BuildKeyboard()
        {
            if (!_viewConstructed) return;
            _keys = MidiInstrumentsManager.BuildKeys(_octave, _manager.Instrument.IsDrumKit);
            PianoCanvas.Children.Clear();
            _keyViews.Clear();
            bool names = ChkShowNoteNames.IsChecked == true;
            bool letters = ChkShowComputerKeys.IsChecked == true;

            foreach (var key in _keys.Where(k => !k.IsBlack).Concat(_keys.Where(k => k.IsBlack)))
            {
                var labels = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, key.IsBlack ? 6 : 8),
                    IsHitTestVisible = false,
                };
                if (letters && key.ComputerKey.Length > 0)
                {
                    var chip = new Border
                    {
                        Padding = new Thickness(4, 0, 4, 1),
                        Margin = new Thickness(0, 0, 0, 3),
                        CornerRadius = new CornerRadius(4),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        BorderThickness = new Thickness(1),
                        Child = new TextBlock { Text = key.ComputerKey, FontSize = 10, FontWeight = FontWeights.SemiBold },
                    };
                    chip.SetResourceReference(Border.BorderBrushProperty, key.IsBlack ? "PianoBlackKeyText" : "PianoWhiteKeyEdge");
                    ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, key.IsBlack ? "PianoBlackKeyText" : "PianoWhiteKeyText");
                    labels.Children.Add(chip);
                }
                if (names && !key.IsBlack)
                {
                    var name = new TextBlock
                    {
                        Text = key.NoteName,
                        FontSize = 10.5,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        FontWeight = key.Note == 60 ? FontWeights.Bold : FontWeights.Normal,   // middle C
                    };
                    name.SetResourceReference(TextBlock.ForegroundProperty, key.Note % 12 == 0 ? "AccentColor" : "PianoWhiteKeyText");
                    labels.Children.Add(name);
                }

                var view = new Border
                {
                    Tag = key,
                    BorderThickness = new Thickness(1),
                    CornerRadius = key.IsBlack ? new CornerRadius(0, 0, 5, 5) : new CornerRadius(0, 0, 7, 7),
                    Child = labels,
                    RenderTransform = new TranslateTransform(),
                    ToolTip = key.DrumName != null ? $"{key.NoteName} · {key.DrumName}" : key.NoteName,
                };
                Panel.SetZIndex(view, key.IsBlack ? 2 : 1);
                PaintKey(view, key, _lit.Contains(key.Note));
                PianoCanvas.Children.Add(view);
                _keyViews[key.Note] = view;
            }
            LayoutKeys();
        }

        private void PianoCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutKeys();

        /// <summary>Fills the width: white keys share it, black keys sit on the joins, 62% as long.</summary>
        private void LayoutKeys()
        {
            _keyRects.Clear();
            double w = PianoCanvas.ActualWidth, h = PianoCanvas.ActualHeight;
            int whites = _keys.Count(k => !k.IsBlack);
            if (w <= 0 || h <= 0 || whites == 0) return;

            double ww = w / whites;
            double bw = ww * 0.62, bh = h * 0.62;
            foreach (var (note, view) in _keyViews)
            {
                var key = (MidiPianoKey)view.Tag;
                var rect = key.IsBlack
                    ? new Rect((key.WhiteIndex + 1) * ww - bw / 2, 0, bw, bh)
                    : new Rect(key.WhiteIndex * ww, 0, ww, h);
                _keyRects[note] = rect;
                Canvas.SetLeft(view, rect.X + (key.IsBlack ? 0 : 1));
                Canvas.SetTop(view, 0);
                view.Width = Math.Max(1, rect.Width - (key.IsBlack ? 0 : 2));
                view.Height = rect.Height;
            }
        }

        private void PaintKey(Border view, MidiPianoKey key, bool down)
        {
            if (down)
            {
                view.SetResourceReference(Border.BackgroundProperty, "AccentColor");
                view.SetResourceReference(Border.BorderBrushProperty, "AccentColorPressed");
            }
            else
            {
                view.SetResourceReference(Border.BackgroundProperty, key.IsBlack ? "PianoBlackKey" : "PianoWhiteKey");
                view.SetResourceReference(Border.BorderBrushProperty, key.IsBlack ? "PianoBlackKeyEdge" : "PianoWhiteKeyEdge");
            }
            if (view.RenderTransform is TranslateTransform shift) shift.Y = down ? 2 : 0;   // the key dips a little
        }

        private void SetKeyLit(int note, bool down)
        {
            if (_closed) return;
            if (down) _lit.Add(note); else _lit.Remove(note);
            if (_keyViews.TryGetValue(note, out var view)) PaintKey(view, (MidiPianoKey)view.Tag, down);
            UpdateSustainPill();
        }

        private void ShowLastNote(int note, int velocity, MidiNoteSource source)
        {
            if (_closed) return;
            TxtLastNote.Text = MidiInstrumentsManager.NoteName(note);
            string? drum = _manager.Instrument.IsDrumKit && source != MidiNoteSource.Playback ? MidiInstrumentCatalog.DrumName(note) : null;
            string from = source switch
            {
                MidiNoteSource.MidiKeyboard => " · MIDI keyboard",
                MidiNoteSource.Playback => " · playback",
                _ => "",
            };
            TxtLastNoteDetail.Text = (drum ?? $"Velocity {velocity}") + from;
            UiMotion.Pop(TxtLastNote, 0.9);
        }

        /// <summary>Black keys are checked first - they sit on top of the whites.</summary>
        private int? KeyAt(Point p)
        {
            foreach (var key in _keys.Where(k => k.IsBlack))
                if (_keyRects.TryGetValue(key.Note, out var r) && r.Contains(p)) return key.Note;
            foreach (var key in _keys.Where(k => !k.IsBlack))
                if (_keyRects.TryGetValue(key.Note, out var r) && r.Contains(p)) return key.Note;
            return null;
        }

        private int VelocityAt(int note, Point p)
        {
            var mode = ChkTouch.IsChecked == true ? MidiTouchMode.ByClickHeight : MidiTouchMode.Fixed;
            double fraction = _keyRects.TryGetValue(note, out var r) && r.Height > 0 ? (p.Y - r.Top) / r.Height : 1;
            return MidiInstrumentsManager.VelocityFor(mode, _manager.Velocity, fraction);
        }

        private void PianoCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Focus();   // so the computer keys play straight after
            var p = e.GetPosition(PianoCanvas);
            if (KeyAt(p) is not int note) return;
            PianoCanvas.CaptureMouse();
            _mouseNote = note;
            _manager.NoteDown(note, VelocityAt(note, p), MidiNoteSource.Mouse);
            e.Handled = true;
        }

        /// <summary>Dragging across the keys plays each one in turn (a glissando).</summary>
        private void PianoCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_mouseNote is not int current || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(PianoCanvas);
            int? note = KeyAt(p);
            if (note == current) return;
            _manager.NoteUp(current);
            _mouseNote = note;
            if (note is int n) _manager.NoteDown(n, VelocityAt(n, p), MidiNoteSource.Mouse);
        }

        private void PianoCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_mouseNote is int note) _manager.NoteUp(note);
            _mouseNote = null;
            if (PianoCanvas.IsMouseCaptured) PianoCanvas.ReleaseMouseCapture();
        }

        private void PianoCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_mouseNote is int note) _manager.NoteUp(note);
            _mouseNote = null;
        }

        // =================================================================
        //  Computer keyboard
        // =================================================================

        /// <summary>Typing in the search box types; otherwise letters play. Arrows change octave unless a list or slider has the focus.</summary>
        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            switch (key)
            {
                case Key.Space:
                    if (!e.IsRepeat)
                    {
                        _manager.SetSustainPedal(true);
                        UpdateSustainPill();
                    }
                    e.Handled = true;   // also stops Space from pressing a focused button
                    return;
                case Key.Escape:
                    Silence();
                    e.Handled = true;
                    return;
                case Key.Left or Key.Right when Keyboard.FocusedElement is not (Slider or ListBoxItem or ListBox or ComboBox or ComboBoxItem):
                    SetOctave(_octave + (key == Key.Left ? -1 : 1));
                    e.Handled = true;
                    return;
            }

            string name = key.ToString();
            if (_manager.ComputerKeyDown(name, _octave) != null || _manager.IsComputerKeyDown(name)) e.Handled = true;
        }

        private void Root_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Space)
            {
                _manager.SetSustainPedal(false);
                UpdateSustainPill();
                if (Keyboard.FocusedElement is not TextBox) e.Handled = true;
                return;
            }
            if (_manager.ComputerKeyUp(key.ToString()) != null) e.Handled = true;
        }

        // =================================================================
        //  Devices
        // =================================================================

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.Pop(IconRefresh, 0.6);
            RefreshDevices();
        }

        /// <summary>Lists synths and keyboards and opens the remembered (or best) ones.</summary>
        private void RefreshDevices()
        {
            var outputs = MidiInstrumentsManager.Outputs();
            var inputs = MidiInstrumentsManager.Inputs();
            var output = MidiInstrumentsManager.PreferredOutput(outputs, _settings.OutputDevice);
            var input = MidiInstrumentsManager.PreferredInput(inputs, _settings.InputDevice);

            _applying = true;
            try
            {
                CmbOutput.ItemsSource = outputs;
                CmbOutput.SelectedItem = output;
                CmbOutput.IsEnabled = outputs.Count > 0;
                var inputItems = new List<MidiDeviceInfo> { NoInputItem };
                inputItems.AddRange(inputs);
                CmbInput.ItemsSource = inputItems;
                CmbInput.SelectedItem = input ?? NoInputItem;
                CmbInput.IsEnabled = inputs.Count > 0;
            }
            finally
            {
                _applying = false;
            }

            string? outError = output == null ? null : _manager.OutputName == output.Name ? null : _manager.OpenOutput(output);
            string? inError = input?.Name == _manager.InputName ? null : _manager.OpenInput(input);
            UpdateDeviceUi(outError, inError, inputs.Count);
        }

        private void CmbOutput_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applying || CmbOutput.SelectedItem is not MidiDeviceInfo device) return;
            string? error = _manager.OpenOutput(device);
            if (error == null) _settings.OutputDevice = device.Name;
            UpdateDeviceUi(error, null, CmbInput.Items.Count - 1);
            SaveSettings();
        }

        private void CmbInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applying || CmbInput.SelectedItem is not MidiDeviceInfo device) return;
            bool none = device.Index < 0;
            string? error = _manager.OpenInput(none ? null : device);
            _settings.InputDevice = none ? MidiInstrumentsManager.NoInput : device.Name;
            UpdateDeviceUi(null, error, CmbInput.Items.Count - 1);
            SaveSettings();
        }

        private void UpdateDeviceUi(string? outError, string? inError, int inputCount)
        {
            bool hasSynth = _manager.OutputName != null;
            NoSynthPanel.Visibility = hasSynth ? Visibility.Collapsed : Visibility.Visible;
            if (outError != null) TxtNoSynth.Text = outError;

            string? keyboard = _manager.InputName;
            if (!hasSynth)
            {
                DevicePill.SetResourceReference(Border.BackgroundProperty, "StatusErrorBg");
                TxtDevice.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                IconDevice.Kind = PackIconKind.MusicNoteOff;
                TxtDevice.Text = "No sound";
            }
            else if (keyboard != null)
            {
                DevicePill.SetResourceReference(Border.BackgroundProperty, "StatusSuccessBg");
                TxtDevice.SetResourceReference(TextBlock.ForegroundProperty, "StatusSuccessFg");
                IconDevice.Kind = PackIconKind.Midi;
                TxtDevice.Text = keyboard;
            }
            else
            {
                DevicePill.SetResourceReference(Border.BackgroundProperty, "RowBg");
                TxtDevice.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                IconDevice.Kind = PackIconKind.KeyboardOutline;
                TxtDevice.Text = "Mouse and computer keys";
            }

            TxtDeviceHint.Text = inError != null
                ? inError
                : outError != null
                    ? outError
                    : inputCount == 0
                        ? "No MIDI keyboard found. Plug one in, then press Look again (top right)."
                        : keyboard != null
                            ? "Your keyboard plays the sound chosen above, on any channel; its pedal works too."
                            : "Pick your keyboard to play with it.";
            TxtDeviceHint.SetResourceReference(TextBlock.ForegroundProperty, inError != null || outError != null ? "StatusErrorFg" : "SidebarTextMuted");
        }

        // =================================================================
        //  Recorder
        // =================================================================

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.IsRecording)
            {
                StopRecording();
                return;
            }
            if (_manager.IsPlaying) return;
            if (_take is { IsEmpty: false } && !_takeSaved &&
                MessageBox.Show(Window.GetWindow(this)!, "Record over the last take? It hasn't been saved.", DialogTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            _manager.StartRecording();
            _pulse = 0;
            _recorderTimer.Start();
            IconRecord.Kind = PackIconKind.Stop;
            TxtRecordLabel.Text = "Stop";
            TxtRecorderTime.Text = "0:00";
            TxtRecorderStatus.Text = $"Recording {_manager.Instrument.Name}…";
            UiMotion.Pop(BtnRecord, 0.94);
            UpdateRecorderButtons();
            Focus();
        }

        private void StopRecording()
        {
            var take = _manager.StopRecording();
            _recorderTimer.Stop();
            IconRecord.Kind = PackIconKind.RecordCircleOutline;
            TxtRecordLabel.Text = "Record";
            if (take == null || take.IsEmpty)
            {
                TxtRecorderStatus.Text = "Nothing was played, so nothing was kept.";
                TxtRecorderTime.Text = _take == null ? "0:00" : MidiInstrumentsManager.FormatDuration(_take.DurationMs);
            }
            else
            {
                _take = take;
                _takeSaved = false;
                TxtRecorderTime.Text = MidiInstrumentsManager.FormatDuration(take.DurationMs);
                TxtRecorderStatus.Text = $"{take.NoteCount:N0} {(take.NoteCount == 1 ? "note" : "notes")} · {take.InstrumentName} · not saved yet";
                UiMotion.Pop(TxtRecorderTime, 0.9);
            }
            UpdateRecorderButtons();
        }

        private void RecorderTimer_Tick(object? sender, EventArgs e)
        {
            if (!_manager.IsRecording) return;
            TxtRecorderTime.Text = MidiInstrumentsManager.FormatDuration(_manager.RecordingElapsedMs);
            if (++_pulse % 10 == 0) UiMotion.Pop(IconRecord, 0.7);   // a gentle heartbeat while recording
        }

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.IsPlaying)
            {
                _manager.StopPlayback();
                return;
            }
            if (_take == null || _manager.IsRecording) return;

            var take = _take;
            IconPlay.Kind = PackIconKind.Stop;
            TxtPlayLabel.Text = "Stop";
            UpdateRecorderButtons(playing: true);
            var progress = new Progress<long>(ms => TxtRecorderTime.Text = MidiInstrumentsManager.FormatDuration(ms));
            try
            {
                await _manager.PlayAsync(take, progress);
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            catch (Exception ex) when (ex is InvalidOperationException or MidiDeviceException)
            {
                TxtRecorderStatus.Text = "Couldn't play: " + ex.Message;
            }
            if (_closed) return;
            IconPlay.Kind = PackIconKind.Play;
            TxtPlayLabel.Text = "Play";
            TxtRecorderTime.Text = MidiInstrumentsManager.FormatDuration(take.DurationMs);
            UpdateRecorderButtons();
        }

        private void BtnSaveMidi_Click(object sender, RoutedEventArgs e)
        {
            if (_take == null) return;
            var dialog = new SaveFileDialog
            {
                Title = "Save the recording",
                Filter = "MIDI file (*.mid)|*.mid",
                FileName = MidiInstrumentsManager.SuggestedFileName(DateTime.Now, _take.InstrumentName),
                DefaultExt = ".mid",
                AddExtension = true,
            };
            if (_lastSaveFolder != null && Directory.Exists(_lastSaveFolder)) dialog.InitialDirectory = _lastSaveFolder;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            try
            {
                MidiInstrumentsManager.SaveMidiFile(_take, dialog.FileName);
                _takeSaved = true;
                _lastSaveFolder = Path.GetDirectoryName(dialog.FileName);
                TxtRecorderStatus.Text = $"Saved {Path.GetFileName(dialog.FileName)}";
                UiMotion.Pop(BtnSaveMidi, 0.94);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this)!, $"Couldn't save the file: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UpdateRecorderButtons(bool playing = false)
        {
            bool recording = _manager.IsRecording;
            playing |= _manager.IsPlaying;
            BtnRecord.IsEnabled = !playing;
            BtnPlay.IsEnabled = _take != null && !recording;
            BtnSaveMidi.IsEnabled = _take != null && !recording && !playing;
        }

        // =================================================================
        //  All sound off
        // =================================================================

        private void BtnPanic_Click(object sender, RoutedEventArgs e)
        {
            Silence();
            UiMotion.Pop(BtnPanic, 0.92);
            Focus();
        }

        /// <summary>Stops playback and every note, and turns sustain off.</summary>
        private void Silence()
        {
            _manager.StopPlayback();
            _mouseNote = null;
            _manager.Panic();
            _applying = true;
            ChkSustain.IsChecked = false;
            _applying = false;
            foreach (int note in _lit.ToList()) SetKeyLit(note, false);
            UpdateSustainPill();
        }
    }
}
