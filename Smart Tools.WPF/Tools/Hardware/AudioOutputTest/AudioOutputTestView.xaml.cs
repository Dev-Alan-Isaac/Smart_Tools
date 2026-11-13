using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Audio Output Tester - UI only. The signals are made by <see cref="AudioSignalGenerator"/>, the sound
    /// card by <see cref="AudioOutputTestManager"/>. Anything that is playing stops when the page is left,
    /// so a test tone never keeps running behind another tool.
    /// </summary>
    public partial class AudioOutputTestView : UserControl
    {
        private sealed class Tile
        {
            public required Border Frame;
            public required TextBlock Short;
            public required Border Meter;
            public required Border MeterTrack;
            public required int Index;
        }

        private readonly AudioOutputTestSettings _settings;
        private readonly DispatcherTimer _meterTimer;
        private readonly DispatcherTimer _statusTimer;
        private readonly List<Tile> _tiles = new();
        private readonly bool _viewConstructed;

        private List<AudioOutputDevice> _devices = new();
        private AudioOutputDevice? _device;
        private AudioOutputSession? _session;
        private uint _mask = uint.MaxValue;
        private bool _playing;
        private bool _loadedOnce;
        private bool _settingControls;

        private enum StatusKind { Success, Error, Info }

        public AudioOutputTestView()
        {
            InitializeComponent();

            _settings = AudioOutputTestManager.LoadSettings();

            _meterTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
            _meterTimer.Tick += (_, _) => UpdateMeters();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                UiMotion.FadeVisibility(BorderStatus, false);
            };

            ApplySettingsToControls();
            if (_settings.SidebarCollapsed) Sidebar.IsCollapsed = true;

            _viewConstructed = true;
            UpdateValueTexts();
            UpdateTestPanels();
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _meterTimer.Stop();
            _statusTimer.Stop();
            CloseSession();
            SaveSettings();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            RefreshDevices();
        }

        private void Root_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Leaving the page silences it.
            if (!IsVisible && _playing) SetPlaying(false);
        }

        // =================================================================
        //  Settings <-> controls
        // =================================================================

        private void ApplySettingsToControls()
        {
            _settingControls = true;
            RadioTestChannels.IsChecked = _settings.Kind == AudioTestKind.Channels;
            RadioTestPhase.IsChecked = _settings.Kind == AudioTestKind.Phase;
            RadioTestTone.IsChecked = _settings.Kind == AudioTestKind.Tone;
            RadioTestSweep.IsChecked = _settings.Kind == AudioTestKind.Sweep;
            RadioTestNoise.IsChecked = _settings.Kind == AudioTestKind.Noise;

            SliderLevel.Value = _settings.LevelDb;
            SliderFreq.Value = AudioOutputTestManager.FrequencyToSlider(_settings.Frequency);
            SliderSweep.Value = _settings.SweepSeconds;
            RadioWaveSine.IsChecked = _settings.Waveform == AudioWaveform.Sine;
            RadioWaveSquare.IsChecked = _settings.Waveform == AudioWaveform.Square;
            RadioWaveTriangle.IsChecked = _settings.Waveform == AudioWaveform.Triangle;
            RadioWaveSaw.IsChecked = _settings.Waveform == AudioWaveform.Saw;
            RadioNoisePink.IsChecked = _settings.Noise == AudioNoiseColor.Pink;
            RadioNoiseWhite.IsChecked = _settings.Noise == AudioNoiseColor.White;
            RadioPhaseIn.IsChecked = _settings.Phase == AudioPhaseMode.InPhase;
            RadioPhaseOut.IsChecked = _settings.Phase == AudioPhaseMode.OutOfPhase;
            RadioPhaseAlt.IsChecked = _settings.Phase == AudioPhaseMode.Alternate;
            RadioPhaseClick.IsChecked = _settings.Phase == AudioPhaseMode.Click;
            _settingControls = false;
        }

        private AudioTestKind CurrentKind =>
            RadioTestPhase.IsChecked == true ? AudioTestKind.Phase
            : RadioTestTone.IsChecked == true ? AudioTestKind.Tone
            : RadioTestSweep.IsChecked == true ? AudioTestKind.Sweep
            : RadioTestNoise.IsChecked == true ? AudioTestKind.Noise
            : AudioTestKind.Channels;

        private AudioTestConfig ReadConfig() => new(
            CurrentKind,
            _mask,
            SliderLevel.Value,
            AudioOutputTestManager.SliderToFrequency(SliderFreq.Value),
            RadioWaveSquare.IsChecked == true ? AudioWaveform.Square
                : RadioWaveTriangle.IsChecked == true ? AudioWaveform.Triangle
                : RadioWaveSaw.IsChecked == true ? AudioWaveform.Saw
                : AudioWaveform.Sine,
            SliderSweep.Value,
            RadioPhaseIn.IsChecked == true ? AudioPhaseMode.InPhase
                : RadioPhaseOut.IsChecked == true ? AudioPhaseMode.OutOfPhase
                : RadioPhaseClick.IsChecked == true ? AudioPhaseMode.Click
                : AudioPhaseMode.Alternate,
            RadioNoiseWhite.IsChecked == true ? AudioNoiseColor.White : AudioNoiseColor.Pink);

        private void SaveSettings()
        {
            if (!_viewConstructed) return;
            try
            {
                var c = ReadConfig();
                _settings.Kind = c.Kind;
                _settings.LevelDb = c.LevelDb;
                _settings.Frequency = c.Frequency;
                _settings.Waveform = c.Waveform;
                _settings.SweepSeconds = c.SweepSeconds;
                _settings.Phase = c.Phase;
                _settings.Noise = c.Noise;
                _settings.SidebarCollapsed = Sidebar.IsCollapsed;
                AudioOutputTestManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Audio output test settings: {ex.Message}");
            }
        }

        // =================================================================
        //  Options changed
        // =================================================================

        private void Option_Checked(object sender, RoutedEventArgs e) => OptionsChanged();

        private void Option_SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => OptionsChanged();

        private void OptionsChanged()
        {
            if (!_viewConstructed || _settingControls) return;
            UpdateValueTexts();
            _session?.Generator.Configure(ReadConfig());
            UpdateGuide();
        }

        private void Test_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            UpdateTestPanels();
            _session?.Generator.Configure(ReadConfig());
            UiMotion.FadeIn(ChannelPanel);
        }

        private void UpdateValueTexts()
        {
            TxtLevelValue.Text = AudioOutputTestManager.FormatDb(SliderLevel.Value);
            TxtHeroLevel.Text = TxtLevelValue.Text;
            TxtFreqValue.Text = AudioOutputTestManager.FormatFrequency(AudioOutputTestManager.SliderToFrequency(SliderFreq.Value));
            TxtSweepValue.Text = string.Format(CultureInfo.CurrentCulture, "{0} s", (int)SliderSweep.Value);
        }

        /// <summary>Only the controls that belong to the chosen test are shown.</summary>
        private void UpdateTestPanels()
        {
            var kind = CurrentKind;
            PanelTone.Visibility = kind == AudioTestKind.Tone ? Visibility.Visible : Visibility.Collapsed;
            PanelSweep.Visibility = kind == AudioTestKind.Sweep ? Visibility.Visible : Visibility.Collapsed;
            PanelNoise.Visibility = kind == AudioTestKind.Noise ? Visibility.Visible : Visibility.Collapsed;
            PanelPhase.Visibility = kind == AudioTestKind.Phase ? Visibility.Visible : Visibility.Collapsed;
            PanelChannelsNote.Visibility = kind == AudioTestKind.Channels ? Visibility.Visible : Visibility.Collapsed;
            PhaseBadge.Visibility = kind == AudioTestKind.Phase ? Visibility.Visible : Visibility.Collapsed;
            if (kind == AudioTestKind.Phase && !_playing) TxtPhaseState.Text = Str("Str_AudioOut_StateIdle");
            UpdateGuide();
        }

        private void UpdateGuide()
        {
            var c = ReadConfig();
            string key = c.Kind switch
            {
                AudioTestKind.Phase => c.Phase switch
                {
                    AudioPhaseMode.InPhase => "Phase_InPhase",
                    AudioPhaseMode.OutOfPhase => "Phase_Out",
                    AudioPhaseMode.Click => "Phase_Click",
                    _ => "Phase_Alt",
                },
                AudioTestKind.Tone => "Tone",
                AudioTestKind.Sweep => "Sweep",
                AudioTestKind.Noise => "Noise",
                _ => "Channels",
            };
            string titleKey = c.Kind switch
            {
                AudioTestKind.Phase => "Str_AudioOut_TestPhase",
                AudioTestKind.Tone => "Str_AudioOut_TestTone",
                AudioTestKind.Sweep => "Str_AudioOut_TestSweep",
                AudioTestKind.Noise => "Str_AudioOut_TestNoise",
                _ => "Str_AudioOut_TestChannels",
            };
            TxtGuideTitle.Text = Str(titleKey);
            TxtGuide.Text = Str("Str_AudioOut_Guide_" + key);
        }

        // =================================================================
        //  Devices and channels
        // =================================================================

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

        private void RefreshDevices()
        {
            string? keep = _device?.Id;
            _devices = AudioOutputTestManager.ListDevices();

            _settingControls = true;
            CmbDevice.ItemsSource = _devices;
            var pick = _devices.FirstOrDefault(d => d.Id == keep) ?? _devices.FirstOrDefault();
            CmbDevice.SelectedItem = pick;
            _settingControls = false;

            if (pick == null)
            {
                _device = null;
                TxtDeviceInfo.Text = Str("Str_AudioOut_NoDevice");
                BtnPlay.IsEnabled = false;
                BuildTiles(0, 0);
                return;
            }
            BtnPlay.IsEnabled = true;
            SelectDevice(pick);
        }

        private void CmbDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _settingControls || CmbDevice.SelectedItem is not AudioOutputDevice device) return;
            SelectDevice(device);
        }

        private void SelectDevice(AudioOutputDevice device)
        {
            if (_playing) SetPlaying(false);
            CloseSession();
            _device = device;
            TxtDeviceInfo.Text = string.Format(Str("Str_AudioOut_DeviceInfo"), device.Channels, device.SampleRate / 1000.0);
            _mask = AudioOutputTestManager.AllChannels(device.Channels);
            BuildTiles(device.Channels, device.SpeakerMask);
        }

        // =================================================================
        //  The picture that fills with each side's sound
        // =================================================================

        /// <summary>
        /// Any device with one or two channels (speakers, headphones, earphones, a headset) gets the picture, each side filling with
        /// the sound that side plays; devices with more channels keep only the per-channel tiles, which become taller to use the room.
        /// </summary>
        private void ApplyStage()
        {
            bool picture = _device?.Channels is 1 or 2;
            HeroStage.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
            HeroLeft.SetFillResource("AccentColor");
            HeroRight.SetFillResource("AccentColor");
            foreach (var tile in _tiles) tile.Frame.MinHeight = picture ? 96 : 170;
            if (!picture) { HeroLeft.Level = 0; HeroRight.Level = 0; }
        }

        private void BuildTiles(int channels, uint speakerMask)
        {
            ChannelPanel.Children.Clear();
            _tiles.Clear();
            if (channels == 0) return;

            // 2 or 4 channels sit in one row; 6 as 3 + 3; 8 as 4 + 4
            ChannelPanel.Columns = channels <= 4 ? channels : (channels + 1) / 2;

            var names = AudioOutputTestManager.ChannelNames(channels, speakerMask);
            for (int i = 0; i < channels; i++)
            {
                var (englishName, shortName) = names[i];
                string name = TryFindResource("Str_AudioOut_Spk_" + shortName) as string ?? englishName;
                var shortText = new TextBlock { Text = shortName, FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center };
                shortText.SetResourceReference(FrameworkElement.StyleProperty, "GlowReadout");
                var nameText = new TextBlock { Text = name, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                var number = new TextBlock { Text = (i + 1).ToString(CultureInfo.CurrentCulture), FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
                number.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

                // Like the battery bar on Battery Health: a rounded outline with the fill inside
                var gloss = new Border { CornerRadius = new CornerRadius(6), IsHitTestVisible = false };
                gloss.SetResourceReference(Border.BackgroundProperty, "DepthGlossBrush");
                var meter = new Border { Height = 12, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Child = gloss };
                meter.SetResourceReference(Border.BackgroundProperty, "AccentFill");
                var track = new Border { Height = 12, CornerRadius = new CornerRadius(6), Child = meter };
                var outline = new Border { Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(4), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(11), Child = track };
                outline.SetResourceReference(Border.BorderBrushProperty, "DepthEdgeBrush");
                outline.SetResourceReference(Border.BackgroundProperty, "ContentBg");

                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(shortText);
                stack.Children.Add(nameText);
                stack.Children.Add(number);
                stack.Children.Add(outline);

                var frame = new Border
                {
                    MinHeight = 170,
                    MaxHeight = 320,
                    Margin = new Thickness(0, 0, 8, 8),
                    Padding = new Thickness(14, 12, 14, 12),
                    CornerRadius = new CornerRadius(14),
                    BorderThickness = new Thickness(1.5),
                    Cursor = Cursors.Hand,
                    Child = stack,
                    Tag = i,
                    ToolTip = Str("Str_AudioOut_TileTip"),
                };
                frame.MouseLeftButtonUp += Tile_Click;
                _tiles.Add(new Tile { Frame = frame, Short = shortText, Meter = meter, MeterTrack = track, Index = i });
                ChannelPanel.Children.Add(frame);
            }
            RefreshTileSelection();
            ApplyStage();
        }

        private void Tile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: int index }) return;
            _mask ^= 1u << index;
            if ((_mask & AudioOutputTestManager.AllChannels(_tiles.Count)) == 0) _mask |= 1u << index;   // never leave nothing selected
            RefreshTileSelection();
            _session?.Generator.Configure(ReadConfig());
            UiMotion.Pop((FrameworkElement)sender);
        }

        private void BtnAllChannels_Click(object sender, RoutedEventArgs e)
        {
            _mask = AudioOutputTestManager.AllChannels(_tiles.Count);
            RefreshTileSelection();
            _session?.Generator.Configure(ReadConfig());
        }

        private void RefreshTileSelection()
        {
            foreach (var t in _tiles)
            {
                bool on = (_mask & (1u << t.Index)) != 0;
                t.Frame.SetResourceReference(Border.BackgroundProperty, on ? "RowBg" : "ContentBg");
                t.Frame.SetResourceReference(Border.BorderBrushProperty, on ? "AccentColor" : "DepthEdgeBrush");
                t.Frame.Opacity = on ? 1 : 0.6;
                // A selected channel glows in the accent colour; the others sit raised on a soft shadow.
                t.Frame.Effect = on && TryFindResource("AccentColor") is SolidColorBrush accent
                    ? new System.Windows.Media.Effects.DropShadowEffect { Color = accent.Color, BlurRadius = 20, ShadowDepth = 0, Opacity = 0.45 }
                    : (System.Windows.Media.Effects.Effect)TryFindResource("DepthShadow");
            }
        }

        // =================================================================
        //  Playing
        // =================================================================

        private void BtnPlay_Click(object sender, RoutedEventArgs e) => SetPlaying(!_playing);

        private void SetPlaying(bool play)
        {
            if (play)
            {
                if (_device == null) return;
                try
                {
                    _session ??= AudioOutputTestManager.Open(_device);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NAudio.MmException or ArgumentException)
                {
                    CloseSession();
                    ShowStatus(string.Format(Str("Str_AudioOut_OpenFailed"), ex.Message), StatusKind.Error);
                    return;
                }

                _session.Generator.Configure(ReadConfig());
                _session.Generator.IsPlaying = true;
                _meterTimer.Start();
            }
            else if (_session != null)
            {
                _session.Generator.IsPlaying = false;
            }

            _playing = play;
            IconPlay.Kind = play ? PackIconKind.Stop : PackIconKind.Play;
            TxtPlay.Text = Str(play ? "Str_AudioOut_Stop" : "Str_AudioOut_Play");
            if (!play) ShowStatus(Str("Str_AudioOut_Stopped"), StatusKind.Info);
        }

        private void CloseSession()
        {
            _meterTimer.Stop();
            _playing = false;
            IconPlay.Kind = PackIconKind.Play;
            TxtPlay.Text = Str("Str_AudioOut_Play");
            _session?.Dispose();
            _session = null;
            foreach (var t in _tiles) t.Meter.Width = 0;
            HeroLeft.Level = 0;
            HeroRight.Level = 0;
        }

        private void UpdateMeters()
        {
            if (_session == null) return;
            var gen = _session.Generator;
            int active = _playing ? gen.ActiveChannel : -1;
            bool channelsTest = CurrentKind == AudioTestKind.Channels;

            foreach (var t in _tiles)
            {
                float peak = gen.TakePeak(t.Index);
                double width = Math.Clamp(Math.Pow(peak, 0.5) * Math.Max(0, t.MeterTrack.ActualWidth), 0, t.MeterTrack.ActualWidth);
                t.Meter.Width = width;
                double level = Math.Clamp(Math.Pow(peak, 0.5), 0, 1);
                if (t.Index == 0)
                {
                    HeroLeft.Level = level;
                    if (_tiles.Count == 1) HeroRight.Level = level;   // mono (Bluetooth hands-free): one signal feeds both sides
                }
                else if (t.Index == 1) HeroRight.Level = level;

                bool highlight = channelsTest && t.Index == active;
                if (highlight) t.Frame.SetResourceReference(Border.BackgroundProperty, "AccentFill");
                else if ((_mask & (1u << t.Index)) != 0) t.Frame.SetResourceReference(Border.BackgroundProperty, "RowBg");
                if (highlight) t.Short.Foreground = System.Windows.Media.Brushes.White;   // white is fine on the accent fill
                else t.Short.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            }

            if (CurrentKind == AudioTestKind.Phase)
            {
                bool outOfPhase = gen.PhaseState == 1;
                bool click = ReadConfig().Phase == AudioPhaseMode.Click;
                TxtPhaseState.Text = _playing
                    ? Str(click ? (outOfPhase ? "Str_AudioOut_ClickInverted" : "Str_AudioOut_ClickNormal") : (outOfPhase ? "Str_AudioOut_StateOut" : "Str_AudioOut_StateIn"))
                    : Str("Str_AudioOut_StateIdle");
            }

            if (!_playing && _tiles.All(t => t.Meter.Width < 0.5)) _meterTimer.Stop();
        }

        // =================================================================
        //  Status
        // =================================================================

        private void ShowStatus(string message, StatusKind kind)
        {
            TxtStatus.Text = message;
            BorderStatus.ToolTip = message;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, kind switch
            {
                StatusKind.Success => "StatusSuccessFg",
                StatusKind.Error => "StatusErrorFg",
                _ => "TextSecondary",
            });
            IconStatus.Kind = kind switch
            {
                StatusKind.Success => PackIconKind.CheckCircleOutline,
                StatusKind.Error => PackIconKind.AlertCircleOutline,
                _ => PackIconKind.InformationOutline,
            };

            if (BorderStatus.Visibility != Visibility.Visible) UiMotion.FadeVisibility(BorderStatus, true);
            else UiMotion.Pop(BorderStatus);

            _statusTimer.Stop();
            _statusTimer.Interval = TimeSpan.FromSeconds(kind == StatusKind.Error ? 9 : 4);
            _statusTimer.Start();
        }

        private string Str(string key) => TryFindResource(key) as string ?? key;
    }
}
