using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Instrument Tuner - UI only. Listening and the reference tone are in
    /// <see cref="InstrumentTunerManager"/>; pitch detection, naming,
    /// smoothing and presets in InstrumentTunerManager.Pitch.cs.
    /// </summary>
    public partial class InstrumentTunerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(450);

        private readonly InstrumentTunerManager _tuner = new();
        private readonly InstrumentTunerNeedle _needle = new();
        private readonly InstrumentTunerInTune _inTune = new();
        private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly List<(Border Chip, TextBlock Name, TextBlock Hz, TextBlock Cents)> _stringChips = new();
        private readonly bool _viewConstructed;

        private InstrumentTunerSettings _settings = new();
        private InstrumentTunerPreset _preset = InstrumentTunerManager.Presets[0];
        private DateTime _lastNoteUtc = DateTime.MinValue;
        private int? _shownNote;
        private int _toneString = -1;
        private bool _loadedOnce;
        private bool _applying;
        private bool _closed;
        private bool _optionsCollapsed;

        public InstrumentTunerView()
        {
            InitializeComponent();
            CmbInstrumentPreset.ItemsSource = InstrumentTunerManager.Presets;
            _tuner.ReadingAvailable += reading => Dispatcher.BeginInvoke(() => ShowReading(reading));
            _tuner.Failed += message => Dispatcher.BeginInvoke(() => ListeningStopped(message, error: true));
            _holdTimer.Tick += HoldTimer_Tick;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: lets go of the microphone and stops a reference tone.</summary>
        public void CancelActiveRun()
        {
            _closed = true;
            _holdTimer.Stop();
            _tuner.Dispose();
        }

        // =================================================================
        //  Lifetime and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce || _closed) return;
            ApplySettings(InstrumentTunerManager.LoadSettings());
            LoadDevices();
            _loadedOnce = true;
            UpdateListenUi();
        }

        /// <summary>
        /// Leaving the page stops listening and the tone, and the page shows
        /// that. (The old page disposed the tuner but kept its buttons in the
        /// "listening" state, so coming back showed Stop for a dead microphone.)
        /// </summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_closed) return;
            StopTone();
            if (_tuner.IsListening) ListeningStopped(Loc.T("Str_InstrumentTuner_MsgStoppedAway", "Stopped while you were on another page."), error: false);
        }

        private void ApplySettings(InstrumentTunerSettings s)
        {
            _applying = true;
            try
            {
                _settings = s;
                SldA4Reference.Value = s.A4Hz;
                TxtA4Value.Text = $"{s.A4Hz:0} Hz";
                BtnNotationSolfege.IsChecked = s.Notation == InstrumentTunerNotation.Solfege;
                BtnNotationLetter.IsChecked = s.Notation != InstrumentTunerNotation.Solfege;
                RadioSensLow.IsChecked = s.Sensitivity == InstrumentTunerSensitivity.Low;
                RadioSensNormal.IsChecked = s.Sensitivity == InstrumentTunerSensitivity.Normal;
                RadioSensHigh.IsChecked = s.Sensitivity == InstrumentTunerSensitivity.High;
                _tuner.Sensitivity = s.Sensitivity;
                CmbInstrumentPreset.SelectedItem = InstrumentTunerManager.FindPreset(s.Preset);
            }
            finally
            {
                _applying = false;
            }
            SetPreset((InstrumentTunerPreset)CmbInstrumentPreset.SelectedItem);
        }

        private void SaveSettings()
        {
            if (_applying || !_loadedOnce || _closed) return;
            _settings.A4Hz = SldA4Reference.Value;
            _settings.Notation = Notation;
            _settings.Preset = _preset.Name;
            _settings.Sensitivity = _tuner.Sensitivity;
            if (CmbInputDevice.SelectedItem is InstrumentTunerDevice device) _settings.DeviceName = device.Name;
            try
            {
                InstrumentTunerManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Tuner settings not saved: {ex.Message}");
            }
        }

        private InstrumentTunerNotation Notation =>
            BtnNotationSolfege.IsChecked == true ? InstrumentTunerNotation.Solfege : InstrumentTunerNotation.Letters;

        private double A4 => SldA4Reference.Value;

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            if (BtnCollapseOptions.Content is StackPanel header)
                header.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        // =================================================================
        //  Microphone
        // =================================================================

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.Pop(IconRefresh, 0.6);
            LoadDevices();
        }

        private void LoadDevices()
        {
            IReadOnlyList<InstrumentTunerDevice> devices;
            try
            {
                devices = InstrumentTunerManager.Devices();
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
            {
                devices = Array.Empty<InstrumentTunerDevice>();
            }
            _applying = true;
            try
            {
                CmbInputDevice.ItemsSource = devices;
                CmbInputDevice.SelectedItem = InstrumentTunerManager.PreferredDevice(devices, _settings.DeviceName);
            }
            finally
            {
                _applying = false;
            }
            TxtMicHint.Text = devices.Count > 0
                ? Loc.T("Str_InstrumentTuner_PlayOneStringAt", "Play one string at a time, close to the microphone.")
                : Loc.T("Str_InstrumentTuner_MsgNoMicFound", "No microphone found. Plug one in, then press Look again (top right).");
            TxtMicHint.SetResourceReference(TextBlock.ForegroundProperty, devices.Count > 0 ? "SidebarTextMuted" : "StatusErrorFg");
            UpdateListenUi();
        }

        private void CmbInputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            SaveSettings();
            if (_tuner.IsListening)
            {
                // Switch microphones straight away.
                _tuner.Stop();
                StartListening();
            }
        }

        private void Sensitivity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out InstrumentTunerSensitivity s))
            {
                _tuner.Sensitivity = s;
                SaveSettings();
            }
        }

        private void BtnListen_Click(object sender, RoutedEventArgs e)
        {
            if (_tuner.IsListening) ListeningStopped(Loc.T("Str_InstrumentTuner_MsgStopped", "Stopped."), error: false);
            else StartListening();
        }

        private void StartListening()
        {
            if (CmbInputDevice.SelectedItem is not InstrumentTunerDevice device)
            {
                ShowStatus(Loc.T("Str_InstrumentTuner_MsgNoMic", "No microphone to listen to."), error: true);
                return;
            }
            try
            {
                _tuner.Start(device);
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or ArgumentException)
            {
                ShowStatus(Loc.F("Str_InstrumentTuner_MsgOpenFailed", "Couldn't open {0}: {1} It may be in use, or blocked in Windows' privacy settings.", device.Name, ex.Message), error: true);
                UpdateListenUi();
                return;
            }
            _holdTimer.Start();
            ShowStatus(Loc.F("Str_InstrumentTuner_MsgListening", "Listening to {0} - play a note.", device.Name));
            UiMotion.Pop(BtnListen, 0.94);
            UpdateListenUi();
        }

        private void ListeningStopped(string message, bool error)
        {
            if (_closed) return;
            _tuner.Stop();
            _holdTimer.Stop();
            ResetDisplay();
            SetLevel(0);
            ShowStatus(message, error);
            UpdateListenUi();
        }

        private void UpdateListenUi()
        {
            bool listening = _tuner.IsListening;
            IconListen.Kind = listening ? PackIconKind.Stop : PackIconKind.Microphone;
            TxtListenLabel.Text = listening ? Loc.T("Str_InstrumentTuner_Stop", "Stop") : Loc.T("Str_InstrumentTuner_StartTuning", "Start tuning");
            BtnListen.IsEnabled = listening || CmbInputDevice.SelectedItem is InstrumentTunerDevice;
            BtnRefreshDevices.IsEnabled = !listening;
        }

        private void ShowStatus(string text, bool error = false)
        {
            TxtMicStatus.Text = text;
            TxtMicStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "TextSecondary");
        }

        private void SetLevel(double rms)
        {
            // RMS 0.001..0.3 on a log scale feels right for a level bar.
            double v = rms <= 0.001 ? 0 : Math.Clamp((Math.Log10(rms) + 3) / 2.5, 0, 1);
            LevelScale.ScaleX = v;
        }

        // =================================================================
        //  Readings
        // =================================================================

        private void ShowReading(InstrumentTunerReading reading)
        {
            if (_closed || !_tuner.IsListening) return;
            SetLevel(reading.Level);
            if (reading.FrequencyHz is not double freq) return;   // the hold timer clears after a moment

            _lastNoteUtc = DateTime.UtcNow;
            var note = InstrumentTunerManager.Name(freq, A4);

            // With an instrument chosen, measure against the nearest string, so
            // a badly flat string still reads as that string, not the note below.
            int stringIndex = -1;
            double cents = note.Cents;
            int targetMidi = note.MidiNote;
            if (!_preset.IsChromatic)
            {
                var (index, stringCents) = InstrumentTunerManager.NearestString(_preset, freq, A4);
                if (Math.Abs(stringCents) <= 100)
                {
                    stringIndex = index;
                    cents = stringCents;
                    targetMidi = _preset.Strings[index].MidiNote;
                }
            }

            double smoothed = _needle.Update(targetMidi, cents);
            bool inTune = _inTune.Update(smoothed);

            string name = InstrumentTunerManager.NoteName(targetMidi, Notation);
            int octaveDigits = name.Length - name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '-').Length;
            TxtNoteName.Text = name[..^octaveDigits];
            TxtOctave.Text = name[^octaveDigits..];
            if (_shownNote != targetMidi)
            {
                _shownNote = targetMidi;
                UiMotion.Pop(TxtNoteName, 0.9);
            }

            string accuracy = Math.Abs(smoothed) <= InstrumentTunerInTune.OnCents ? "StatusSuccessFg"
                : Math.Abs(smoothed) <= 15 ? "StatusWarningFg" : "StatusErrorFg";
            TxtNoteName.SetResourceReference(TextBlock.ForegroundProperty, accuracy);
            TxtGuidance.SetResourceReference(TextBlock.ForegroundProperty, accuracy);
            TxtGuidance.Text = inTune ? Loc.T("Str_InstrumentTuner_InTune", "In tune") : smoothed < 0 ? Loc.T("Str_InstrumentTuner_TooLow", "Too low - tighten / tune up ↑") : Loc.T("Str_InstrumentTuner_TooHigh", "Too high - loosen / tune down ↓");
            TxtFrequency.Text = Loc.F("Str_InstrumentTuner_FreqTarget", "{0:0.0} Hz  ·  target {1:0.0} Hz", freq, InstrumentTunerManager.FrequencyOf(targetMidi, A4));
            TxtCentsValue.Text = $"{smoothed:+0;−0;0}¢";

            PositionNeedle(smoothed, active: true);
            HighlightString(stringIndex, smoothed);

            bool wasShown = InTunePill.Visibility == Visibility.Visible;
            InTunePill.Visibility = inTune ? Visibility.Visible : Visibility.Collapsed;
            if (inTune && !wasShown) UiMotion.Pop(InTunePill, 0.7);
        }

        /// <summary>No note for a moment: fade back to "waiting" (holding briefly stops it flashing between notes).</summary>
        private void HoldTimer_Tick(object? sender, EventArgs e)
        {
            if (_shownNote != null && DateTime.UtcNow - _lastNoteUtc > HoldDuration) ResetDisplay();
        }

        private void ResetDisplay()
        {
            _shownNote = null;
            _needle.Reset();
            _inTune.Reset();
            TxtNoteName.Text = "–";
            TxtOctave.Text = "";
            TxtNoteName.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            TxtGuidance.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            TxtGuidance.Text = _tuner.IsListening ? Loc.T("Str_InstrumentTuner_WaitingForANote", "Waiting for a note") : Loc.T("Str_InstrumentTuner_NotListening", "Not listening");
            TxtFrequency.Text = "";
            TxtCentsValue.Text = "0¢";
            InTunePill.Visibility = Visibility.Collapsed;
            PositionNeedle(0, active: false);
            HighlightString(-1, 0);
        }

        private double _needleCents;
        private bool _needleActive;

        private void PositionNeedle(double cents, bool active)
        {
            _needleCents = cents;
            _needleActive = active;
            double width = CentsMeterCanvas.ActualWidth;
            if (width <= 0) return;
            Canvas.SetLeft(CentsMeterNeedle, (cents + 50) / 100 * width - CentsMeterNeedle.Width / 2);
            CentsMeterNeedle.Opacity = active ? 1 : 0.35;
        }

        private void CentsMeterCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => PositionNeedle(_needleCents, _needleActive);

        // =================================================================
        //  Instrument and strings
        // =================================================================

        private void CmbInstrumentPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying || CmbInstrumentPreset.SelectedItem is not InstrumentTunerPreset preset) return;
            SetPreset(preset);
            SaveSettings();
        }

        private void SetPreset(InstrumentTunerPreset preset)
        {
            StopTone();
            _preset = preset;
            IconPreset.Kind = Enum.TryParse(preset.Icon, out PackIconKind kind) ? kind : PackIconKind.MusicNote;
            TxtPresetHint.Text = preset.IsChromatic
                ? Loc.T("Str_InstrumentTuner_NamesAnyNote", "Names any note you play.")
                : Loc.F("Str_InstrumentTuner_StringsList", "{0} strings: {1}", preset.Strings.Count, string.Join(" ", preset.Strings.Select(s => InstrumentTunerManager.NoteName(s.MidiNote, Notation))));
            BuildStrings();
        }

        /// <summary>One chip per string, in the notation and reference pitch chosen; a click plays that note.</summary>
        private void BuildStrings()
        {
            StringStrip.Items.Clear();
            _stringChips.Clear();
            StringsPanel.Visibility = _preset.IsChromatic ? Visibility.Collapsed : Visibility.Visible;
            for (int i = 0; i < _preset.Strings.Count; i++)
            {
                var s = _preset.Strings[i];
                var name = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Text = InstrumentTunerManager.NoteName(s.MidiNote, Notation) };
                var hz = new TextBlock { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Text = $"{InstrumentTunerManager.FrequencyOf(s.MidiNote, A4):0.0} Hz" };
                var cents = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Text = " " };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                hz.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                var chip = new Border
                {
                    MinWidth = 70,
                    Margin = new Thickness(4, 0, 4, 0),
                    Padding = new Thickness(12, 8, 12, 6),
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(12),
                    Cursor = Cursors.Hand,
                    Tag = i,
                    ToolTip = Loc.T("Str_InstrumentTuner_ChipTip", "Click to hear this note; click again to stop"),
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                    Child = new StackPanel { Orientation = Orientation.Vertical, Children = { name, hz, cents } },
                };
                chip.MouseLeftButtonUp += StringChip_Click;
                StringStrip.Items.Add(chip);
                _stringChips.Add((chip, name, hz, cents));
            }
            HighlightString(-1, 0);
        }

        private void HighlightString(int index, double cents)
        {
            for (int i = 0; i < _stringChips.Count; i++)
            {
                var (chip, _, _, centsText) = _stringChips[i];
                bool lit = i == index;
                bool tone = i == _toneString;
                chip.SetResourceReference(Border.BackgroundProperty, lit ? "AccentColorSubtle" : "RowBg");
                chip.SetResourceReference(Border.BorderBrushProperty, tone ? "AccentColor" : lit ? "AccentColor" : "CardBorder");
                centsText.Text = lit ? $"{cents:+0;−0;0}¢" : tone ? "♪ playing" : " ";
                centsText.SetResourceReference(TextBlock.ForegroundProperty,
                    lit ? (Math.Abs(cents) <= InstrumentTunerInTune.OnCents ? "StatusSuccessFg" : "StatusWarningFg") : "AccentColor");
            }
        }

        private void StringChip_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: int index } chip) return;
            if (_toneString == index)
            {
                StopTone();
                return;
            }
            try
            {
                _tuner.PlayTone(InstrumentTunerManager.FrequencyOf(_preset.Strings[index].MidiNote, A4));
                _toneString = index;
                UiMotion.Pop(chip, 0.9);
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
            {
                ShowStatus("Couldn't play the note: " + ex.Message, error: true);
            }
            HighlightString(-1, 0);
        }

        private void StopTone()
        {
            _tuner.StopTone();
            _toneString = -1;
            if (_stringChips.Count > 0) HighlightString(-1, 0);
        }

        // =================================================================
        //  Reference pitch and notation
        // =================================================================

        private void SldA4Reference_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtA4Value.Text = $"{e.NewValue:0} Hz";
            if (_applying) return;
            BuildStrings();   // string frequencies follow A4 (the old strip didn't change)
            if (_toneString >= 0) StopTone();
            SaveSettings();
        }

        private void BtnA4Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tag } && double.TryParse(tag, out double hz)) SldA4Reference.Value = hz;
        }

        /// <summary>Click, not Checked (Checked fires while the XAML loads). Strings and hint follow the notation too.</summary>
        private void Notation_Click(object sender, RoutedEventArgs e)
        {
            SetPreset(_preset);
            _shownNote = null;
            SaveSettings();
        }
    }
}
