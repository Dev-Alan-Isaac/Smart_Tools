using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Text to Speech - UI only. Speaking, word progress and saving WAVs
    /// are in <see cref="TextToSpeechManager"/>; SSML, names and settings in
    /// TextToSpeechManager.Text.cs.
    /// </summary>
    public partial class TextToSpeechView : UserControl
    {
        private static string DialogTitle => Loc.T("Str_Tool_text-to-speech_Name", "Text to Speech");
        private const double SidebarExpandedWidth = 290;
        private const double SidebarCollapsedWidth = 56;

        private readonly bool _viewConstructed;
        private TextToSpeechManager? _manager;
        private TextToSpeechSettings _settings = new();
        // Inside the Output card (built in BuildOutputOptions).
        private TextBox TxtOutputFileName = null!;
        private TextBlock TxtFileNameExample = null!;
        private CheckBox AutoSaveCheckBox = null!;
        private CancellationTokenSource? _saveCts;
        private bool _loadedOnce;
        private bool _applying;
        private bool _closed;
        private bool _sampling;
        private bool _optionsCollapsed;
        private TextToSpeechRequest? _lastRequest;
        private int _lastOffset;

        public TextToSpeechView()
        {
            InitializeComponent();
            FileDrop.Attach(this, LoadTextFile);
            OutputCard.DefaultFolder = TextToSpeechManager.DefaultFolder;
            BuildOutputOptions();
            _viewConstructed = true;
        }

        /// <summary>The Output card's folder (this session only - never saved), else the default.</summary>
        private string SaveFolder => OutputCard.EffectiveFolder ?? TextToSpeechManager.DefaultFolder;

        private void BuildOutputOptions()
        {
            TxtOutputFileName = OutputCardOptions.TextBox("From the first words",
                "\".wav\" is added if missing. A name that's taken gets \" (2)\".");
            TxtOutputFileName.TextChanged += TxtOutputFileName_TextChanged;
            TxtFileNameExample = OutputCardOptions.Hint();
            AutoSaveCheckBox = OutputCardOptions.CheckBox("Save without asking", true,
                "Save straight into the folder above. Off: pick where to save each time.");
            AutoSaveCheckBox.Checked += Option_Changed;
            AutoSaveCheckBox.Unchecked += Option_Changed;
            OutputCard.Options = OutputCardOptions.Panel(
                OutputCardOptions.Label("File name"), TxtOutputFileName, TxtFileNameExample, AutoSaveCheckBox);
        }

        /// <summary>Called from MainWindow.Window_Closing: stops speaking, cancels a save in progress (no half file is left) and releases the voice.</summary>
        public void CancelActiveRun()
        {
            if (_closed) return;
            _closed = true;
            _saveCts?.Cancel();
            _manager?.Dispose();
            _manager = null;
        }

        // =================================================================
        //  Lifetime and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce || _closed) return;
            try
            {
                _manager = new TextToSpeechManager();
                _manager.StateChanged += state => Dispatcher.BeginInvoke(() => ShowState(state));
                _manager.WordReached += position => Dispatcher.BeginInvoke(() => HighlightWord(position));
                _manager.Finished += (cancelled, error) => Dispatcher.BeginInvoke(() => SpeakingFinished(cancelled, error));
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                ShowStatus("Windows speech isn't available on this PC: " + ex.Message, error: true);
            }

            ApplySettings(TextToSpeechManager.LoadSettings());
            LoadVoices();
            _loadedOnce = true;
            UpdateTextStats();
            UpdateButtons();
        }

        /// <summary>Leaving the page stops the voice (its Pause and Stop buttons would be out of reach). A save carries on.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_closed) _manager?.Stop();
        }

        private void ApplySettings(TextToSpeechSettings s)
        {
            _applying = true;
            try
            {
                _settings = s;
                RateSlider.Value = s.Rate;
                VolumeSlider.Value = s.Volume;
                PitchSlider.Value = s.Pitch;
                ChkHighlight.IsChecked = s.HighlightWords;
                AutoSaveCheckBox.IsChecked = s.SaveWithoutAsking;
                TxtOutputFileName.Text = s.FileName;
            }
            finally
            {
                _applying = false;
            }
            UpdateSoundLabels();
            UpdateFileNameExample();
        }

        private void SaveSettings()
        {
            if (_applying || !_loadedOnce || _closed) return;
            _settings.Rate = (int)RateSlider.Value;
            _settings.Volume = (int)VolumeSlider.Value;
            _settings.Pitch = (int)PitchSlider.Value;
            _settings.HighlightWords = ChkHighlight.IsChecked == true;
            _settings.SaveWithoutAsking = AutoSaveCheckBox.IsChecked == true;
            _settings.FileName = TxtOutputFileName.Text.Trim();
            if (VoiceCombo.SelectedItem is TextToSpeechVoice voice) _settings.VoiceName = voice.Name;
            try
            {
                TextToSpeechManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Text to Speech settings not saved: {ex.Message}");
            }
        }

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

        // =================================================================
        //  Voices and sound
        // =================================================================

        private void BtnRefreshVoices_Click(object sender, RoutedEventArgs e)
        {
            UiMotion.Pop(IconRefresh, 0.6);
            LoadVoices();
        }

        private void LoadVoices()
        {
            var voices = _manager?.Voices() ?? Array.Empty<TextToSpeechVoice>();
            _applying = true;
            try
            {
                VoiceCombo.ItemsSource = voices;
                VoiceCombo.SelectedItem = TextToSpeechManager.PreferredVoice(voices, _settings.VoiceName, TextToSpeechManager.CurrentCulture);
            }
            finally
            {
                _applying = false;
            }
            TxtVoiceHint.Text = voices.Count switch
            {
                0 => "No voices installed. Add one in Windows Settings → Time & language → Speech, then press Look again.",
                1 => "One voice installed. Windows Settings → Speech can add more languages.",
                _ => $"{voices.Count} voices installed.",
            };
            TxtVoiceHint.SetResourceReference(TextBlock.ForegroundProperty, voices.Count == 0 ? "StatusErrorFg" : "SidebarTextMuted");
            UpdateVoicePill();
            UpdateButtons();
        }

        private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            UpdateVoicePill();
            SaveSettings();
        }

        private void UpdateVoicePill() =>
            TxtVoicePill.Text = VoiceCombo.SelectedItem is TextToSpeechVoice v ? v.DisplayLabel : "No voice";

        private void Sound_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            UpdateSoundLabels();
            UpdateTextStats();
            SaveSettings();
        }

        private void UpdateSoundLabels()
        {
            int rate = (int)RateSlider.Value, pitch = (int)PitchSlider.Value;
            TxtRateValue.Text = rate == 0 ? "Normal" : (rate > 0 ? "+" : "") + rate;
            TxtVolumeValue.Text = $"{VolumeSlider.Value:0}%";
            TxtPitchValue.Text = pitch == 0 ? "Natural" : TextToSpeechManager.PitchPercent(pitch);
        }

        private void BtnResetSound_Click(object sender, RoutedEventArgs e)
        {
            RateSlider.Value = 0;
            VolumeSlider.Value = 100;
            PitchSlider.Value = 0;
        }

        private void BtnSample_Click(object sender, RoutedEventArgs e)
        {
            if (_manager == null || VoiceCombo.SelectedItem is not TextToSpeechVoice voice) return;
            string name = voice.DisplayLabel.Split(" · ")[0];
            _sampling = true;
            Speak(new TextToSpeechRequest($"Hello, I'm {name}. This is how I sound.", voice, (int)RateSlider.Value, (int)VolumeSlider.Value, (int)PitchSlider.Value), 0);
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateFileNameExample();
            SaveSettings();
        }

        // =================================================================
        //  Speaking
        // =================================================================

        private TextToSpeechRequest RequestFor(string text) =>
            new(text, VoiceCombo.SelectedItem as TextToSpeechVoice, (int)RateSlider.Value, (int)VolumeSlider.Value, (int)PitchSlider.Value);

        /// <summary>Reads the selected text if there is a selection, else everything.</summary>
        private void BtnSpeak_Click(object sender, RoutedEventArgs e)
        {
            if (_manager == null) return;
            if (_manager.State != TextToSpeechState.Ready && _lastRequest != null && !_sampling)
            {
                // "Start again": the highlighted word is a selection now - don't read just that.
                Speak(_lastRequest, _lastOffset);
                return;
            }
            bool selection = TxtSpeech.SelectionLength > 0 && !string.IsNullOrWhiteSpace(TxtSpeech.SelectedText);
            string text = selection ? TxtSpeech.SelectedText : TxtSpeech.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowStatus("Type or paste some text first.", error: true);
                TxtSpeech.Focus();
                return;
            }
            _sampling = false;
            Speak(RequestFor(text), selection ? TxtSpeech.SelectionStart : 0);
        }

        private void Speak(TextToSpeechRequest request, int textOffset)
        {
            try
            {
                if (!_sampling)
                {
                    _lastRequest = request;
                    _lastOffset = textOffset;
                }
                _manager!.Speak(request, textOffset);
                if (!_sampling) TxtSpeech.IsReadOnly = true;   // positions must stay put while the word is highlighted
                ShowStatus(_sampling ? "Playing a sample…" : "Speaking…");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or System.Xml.XmlException)
            {
                _sampling = false;
                ShowStatus("Couldn't speak: " + ex.Message, error: true);
            }
            UpdateButtons();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_manager == null) return;
            if (_manager.State == TextToSpeechState.Paused) _manager.Resume();
            else _manager.Pause();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e) => _manager?.Stop();

        private void ShowState(TextToSpeechState state)
        {
            if (_closed) return;
            IconPause.Kind = state == TextToSpeechState.Paused ? PackIconKind.Play : PackIconKind.Pause;
            StatePill.Visibility = state == TextToSpeechState.Ready ? Visibility.Collapsed : Visibility.Visible;
            TxtStatePill.Text = state == TextToSpeechState.Paused ? "Paused" : "Speaking";
            IconStatePill.Kind = state == TextToSpeechState.Paused ? PackIconKind.Pause : PackIconKind.VolumeHigh;
            if (state == TextToSpeechState.Paused) ShowStatus("Paused.");
            else if (state == TextToSpeechState.Speaking) ShowStatus(_sampling ? "Playing a sample…" : "Speaking…");
            UpdateButtons();
        }

        private void HighlightWord(int position)
        {
            if (_closed || _sampling || ChkHighlight.IsChecked != true) return;
            var (start, length) = TextToSpeechManager.WordAt(TxtSpeech.Text, position);
            if (length == 0) return;
            TxtSpeech.Select(start, length);
            int line = TxtSpeech.GetLineIndexFromCharacterIndex(start);
            if (line >= 0) TxtSpeech.ScrollToLine(line);
        }

        private void SpeakingFinished(bool cancelled, string? error)
        {
            if (_closed) return;
            TxtSpeech.IsReadOnly = false;
            if (!_sampling) TxtSpeech.Select(TxtSpeech.SelectionStart + TxtSpeech.SelectionLength, 0);
            ShowStatus(error != null ? "The voice stopped: " + error : cancelled ? "Stopped." : _sampling ? "That's the voice." : "Finished.", error != null);
            _sampling = false;
            ShowState(TextToSpeechState.Ready);
        }

        // =================================================================
        //  Saving to WAV
        // =================================================================

        private async void BtnSaveToFile_Click(object sender, RoutedEventArgs e)
        {
            if (_saveCts != null)
            {
                _saveCts.Cancel();
                return;
            }

            string text = TxtSpeech.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowStatus("Type or paste some text first.", error: true);
                return;
            }

            string name = TextToSpeechManager.OutputFileName(TxtOutputFileName.Text, text);
            string path;
            if (AutoSaveCheckBox.IsChecked == true)
            {
                if (OutputCard.Status.IsError)
                {
                    ShowStatus("Can't save there: " + OutputCard.Status.Text + ". Choose another folder in Save to.", error: true);
                    return;
                }
                path = TextToSpeechManager.UniquePath(Path.Combine(SaveFolder, name));
            }
            else
            {
                var dialog = new SaveFileDialog { Title = "Save speech", Filter = "WAV audio|*.wav", DefaultExt = ".wav", FileName = name };
                if (Directory.Exists(SaveFolder)) dialog.InitialDirectory = SaveFolder;
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
                path = dialog.FileName;
            }

            var cts = new CancellationTokenSource();
            _saveCts = cts;
            SetSaving(true);
            OutputCard.IsLocked = true;
            ShowStatus($"Saving {Path.GetFileName(path)}…");
            var progress = new Progress<double>(f => ProgressSave.Value = f);
            try
            {
                await TextToSpeechManager.SaveWavAsync(RequestFor(text), path, progress, cts.Token);
                if (!_closed) ShowStatus($"Saved {Path.GetFileName(path)} in {Path.GetDirectoryName(path)}.");
            }
            catch (OperationCanceledException)
            {
                if (!_closed) ShowStatus("Save cancelled - nothing was written.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Xml.XmlException)
            {
                if (!_closed) ShowStatus("Couldn't save: " + ex.Message, error: true);
            }
            finally
            {
                _saveCts = null;
                cts.Dispose();
                if (!_closed)
                {
                    SetSaving(false);
                    OutputCard.IsLocked = false;
                    OutputCard.Refresh();   // free space, or "made on the first save" is now "ready"
                }
            }
        }

        private void SetSaving(bool saving)
        {
            ProgressSave.Value = 0;
            ProgressSave.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
            IconSave.Kind = saving ? PackIconKind.Close : PackIconKind.ContentSaveOutline;
            TxtSaveLabel.Text = saving ? "Cancel save" : "Save as WAV";
            UiMotion.Pop(BtnSaveToFile, 0.95);
        }

        private void TxtOutputFileName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateFileNameExample();
            SaveSettings();
        }

        private void UpdateFileNameExample()
        {
            string name = TextToSpeechManager.OutputFileName(TxtOutputFileName.Text, TxtSpeech.Text);
            TxtFileNameExample.Text = AutoSaveCheckBox.IsChecked == true
                ? $"Saves as \"{name}\" in the folder above."
                : $"You'll be asked where to save (suggested: \"{name}\").";
        }

        // =================================================================
        //  Text box
        // =================================================================

        private void BtnLoadFromFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "Open a text file", Filter = "Text files|*.txt;*.md;*.csv;*.log|All files|*.*" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            LoadTextFile(dialog.FileName);
        }

        /// <summary>Reads a text file into the box (from the Open button or a drop).</summary>
        private void LoadTextFile(string path)
        {
            if (TxtSpeech.IsReadOnly) return;   // not while speaking
            try
            {
                TxtSpeech.Text = TextToSpeechManager.ReadTextFile(path);
                TxtSpeech.CaretIndex = 0;
                ShowStatus($"Opened {Path.GetFileName(path)}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ShowStatus("Couldn't open it: " + ex.Message, error: true);
            }
        }

        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText()) TxtSpeech.Text = Clipboard.GetText();
                else ShowStatus("There's no text on the clipboard.", error: true);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                ShowStatus("The clipboard is busy - try again.", error: true);
            }
        }

        private void BtnClearText_Click(object sender, RoutedEventArgs e)
        {
            if (TxtSpeech.IsReadOnly) return;
            TxtSpeech.Clear();
            TxtSpeech.Focus();
        }

        private void TxtSpeech_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateTextStats();
            UpdateFileNameExample();
            UpdateButtons();
        }

        private void UpdateTextStats()
        {
            string text = TxtSpeech.Text;
            int words = TextToSpeechManager.WordCount(text);
            string length = TextToSpeechManager.DescribeDuration(TextToSpeechManager.EstimatedDuration(text, (int)RateSlider.Value));
            TxtLengthPill.Text = words == 0 ? "0 words" : $"{words:N0} {(words == 1 ? "word" : "words")} · {length}";
            TxtCharCount.Text = $"{text.Length:N0} {(text.Length == 1 ? "character" : "characters")}";
        }

        private void UpdateButtons()
        {
            var state = _manager?.State ?? TextToSpeechState.Ready;
            bool hasVoice = _manager != null && VoiceCombo.SelectedItem is TextToSpeechVoice;
            bool hasText = !string.IsNullOrWhiteSpace(TxtSpeech.Text);
            BtnSpeak.IsEnabled = hasVoice && hasText;
            TxtSpeakLabel.Text = state == TextToSpeechState.Ready ? "Speak" : "Start again";
            BtnPause.IsEnabled = state != TextToSpeechState.Ready;
            BtnStop.IsEnabled = state != TextToSpeechState.Ready;
            BtnSample.IsEnabled = hasVoice;
            BtnSaveToFile.IsEnabled = _saveCts != null || (hasVoice && hasText);
        }

        private void ShowStatus(string text, bool error = false)
        {
            StatusText.Text = text;
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "TextSecondary");
        }
    }
}
