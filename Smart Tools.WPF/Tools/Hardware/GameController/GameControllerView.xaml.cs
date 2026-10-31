using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using ProgressBar = System.Windows.Controls.ProgressBar;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Game Controller Tester - UI only. Reading, dead zones and the three analyses are in
    /// <see cref="GameControllerManager"/>; a poller thread reads the controller about every millisecond.
    /// Vibration and the poller stop when the page is left, so a motor never keeps running behind another tool.
    /// </summary>
    public partial class GameControllerView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double RestSeconds = 3, RangeSeconds = 10, TriggerMaxSeconds = 8;

        private enum Test { None, Rest, Range, Trigger }
        private enum StatusKind { Success, Error, Info }

        private readonly GameControllerSettings _settings;
        private readonly DispatcherTimer _uiTimer;
        private readonly DispatcherTimer _scanTimer;
        private readonly DispatcherTimer _statusTimer;
        private readonly bool _viewConstructed;

        private readonly List<(int Bit, Border Chip, TextBlock Text)> _buttonChips = new();
        private readonly List<(ProgressBar Bar, TextBlock Text)> _axisRows = new();

        private List<ControllerInfo> _controllers = new();
        private ControllerInfo? _current;
        private ControllerPoller? _poller;
        private ulong _shownButtons = ulong.MaxValue;
        private DateTime _lastRate = DateTime.MinValue;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _settingControls;
        private bool _wasConnected;

        // running test
        private Test _test = Test.None;
        private int _testSide;                       // 0 = left / LT, 1 = right / RT
        private DateTime _testStart;
        private readonly List<(double Time, double Value)> _triggerSamples = new();
        private readonly double[][] _liveEnvelope = { new double[GameControllerManager.EnvelopeBuckets], new double[GameControllerManager.EnvelopeBuckets] };
        private readonly StickRestResult?[] _restResults = new StickRestResult?[2];

        public GameControllerView()
        {
            InitializeComponent();

            _settings = GameControllerManager.LoadSettings();

            _uiTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
            _uiTimer.Tick += (_, _) => Tick();

            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _scanTimer.Tick += (_, _) => Rescan();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                UiMotion.FadeVisibility(BorderStatus, false);
            };

            ApplySettingsToControls();
            if (_settings.SidebarCollapsed) SetSidebarCollapsed(true);
            ThemePlots();

            _viewConstructed = true;
            UpdateValueTexts();
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            StopEverything();
            SaveSettings();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            Rescan(initial: true);
            if (IsVisible) StartRunning();
        }

        private void Root_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!_loadedOnce) return;
            if (IsVisible) StartRunning();
            else StopRunning();
        }

        private void StartRunning()
        {
            _uiTimer.Start();
            _scanTimer.Start();
            if (_current != null && _poller == null) _poller = new ControllerPoller(_current);
        }

        /// <summary>Leaving the page: no polling, no running motors.</summary>
        private void StopRunning()
        {
            _uiTimer.Stop();
            _scanTimer.Stop();
            CancelTest();
            ZeroVibration();
            _poller?.Dispose();
            _poller = null;
        }

        private void StopEverything()
        {
            StopRunning();
            _statusTimer.Stop();
        }

        // =================================================================
        //  Theme for the plots (brushes follow theme switches)
        // =================================================================

        private void ThemePlots()
        {
            foreach (var plot in new[] { PlotLeft, PlotRight, PlotLeftTest, PlotRightTest })
            {
                plot.SetResourceReference(StickPlotControl.GridBrushProperty, "TextSecondary");
                plot.SetResourceReference(StickPlotControl.AccentBrushProperty, "AccentFill");
                plot.SetResourceReference(StickPlotControl.DotBrushProperty, "TextPrimary");
                plot.SetResourceReference(StickPlotControl.BackBrushProperty, "RowBg");
            }
            foreach (var chart in new[] { ChartLT, ChartRT })
            {
                chart.SetResourceReference(TriggerChartControl.GridBrushProperty, "TextSecondary");
                chart.SetResourceReference(TriggerChartControl.LineBrushProperty, "AccentFill");
                chart.SetResourceReference(TriggerChartControl.BackBrushProperty, "RowBg");
            }
        }

        // =================================================================
        //  Settings <-> controls
        // =================================================================

        private void ApplySettingsToControls()
        {
            _settingControls = true;
            RadioDzAxial.IsChecked = _settings.Mode == DeadzoneMode.Axial;
            RadioDzRadial.IsChecked = _settings.Mode == DeadzoneMode.Radial;
            RadioDzScaled.IsChecked = _settings.Mode == DeadzoneMode.ScaledRadial;
            SliderDzLeft.Value = _settings.LeftDeadzonePercent;
            SliderDzRight.Value = _settings.RightDeadzonePercent;
            SliderDzTrig.Value = _settings.TriggerThresholdPercent;
            _settingControls = false;
        }

        private DeadzoneMode Mode => RadioDzAxial.IsChecked == true ? DeadzoneMode.Axial : RadioDzRadial.IsChecked == true ? DeadzoneMode.Radial : DeadzoneMode.ScaledRadial;

        private void SaveSettings()
        {
            if (!_viewConstructed) return;
            try
            {
                _settings.Mode = Mode;
                _settings.LeftDeadzonePercent = (int)SliderDzLeft.Value;
                _settings.RightDeadzonePercent = (int)SliderDzRight.Value;
                _settings.TriggerThresholdPercent = (int)SliderDzTrig.Value;
                _settings.SidebarCollapsed = _optionsCollapsed;
                GameControllerManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Game controller settings: {ex.Message}");
            }
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(!_optionsCollapsed);

        private void SetSidebarCollapsed(bool collapsed)
        {
            _optionsCollapsed = collapsed;
            var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(collapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private void Dz_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            UpdateValueTexts();
        }

        private void Dz_SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || _settingControls) return;
            UpdateValueTexts();
        }

        private void UpdateValueTexts()
        {
            TxtDzLeft.Text = $"{(int)SliderDzLeft.Value}%";
            TxtDzRight.Text = $"{(int)SliderDzRight.Value}%";
            TxtDzTrig.Text = $"{(int)SliderDzTrig.Value}%";
            TxtMotorLeft.Text = $"{(int)SliderMotorLeft.Value}%";
            TxtMotorRight.Text = $"{(int)SliderMotorRight.Value}%";
            bool square = Mode == DeadzoneMode.Axial;
            PlotLeft.SquareDeadzone = PlotRight.SquareDeadzone = PlotLeftTest.SquareDeadzone = PlotRightTest.SquareDeadzone = square;
            PlotLeft.Deadzone = PlotLeftTest.Deadzone = SliderDzLeft.Value / 100;
            PlotRight.Deadzone = PlotRightTest.Deadzone = SliderDzRight.Value / 100;
            ChartLT.Threshold = ChartRT.Threshold = SliderDzTrig.Value / 100;
        }

        private void BtnDzDefaults_Click(object sender, RoutedEventArgs e)
        {
            SliderDzLeft.Value = GameControllerManager.XInputLeftDeadzonePercent;
            SliderDzRight.Value = GameControllerManager.XInputRightDeadzonePercent;
            SliderDzTrig.Value = GameControllerManager.XInputTriggerThresholdPercent;
            RadioDzScaled.IsChecked = true;
            ShowStatus(Str("Str_GameCtl_DefaultsApplied"), StatusKind.Info);
        }

        // =================================================================
        //  Finding and choosing controllers
        // =================================================================

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Rescan(initial: false, announce: true);

        /// <summary>Re-reads the list (plugging in or out is noticed); the chosen controller stays chosen if it is still there.</summary>
        private void Rescan(bool initial = false, bool announce = false)
        {
            var found = GameControllerManager.ListControllers();
            bool same = found.Count == _controllers.Count && found.Zip(_controllers).All(p => p.First.Id == p.Second.Id && p.First.Name == p.Second.Name);
            if (same && !initial)
            {
                if (announce) ShowStatus(Str("Str_GameCtl_NothingNew"), StatusKind.Info);
                return;
            }

            string? keep = _current?.Id;
            int before = _controllers.Count;
            _controllers = found;

            _settingControls = true;
            CmbController.ItemsSource = _controllers;
            var pick = _controllers.FirstOrDefault(c => c.Id == keep) ?? _controllers.FirstOrDefault();
            CmbController.SelectedItem = pick;
            _settingControls = false;

            SelectController(pick);
            if (!initial && found.Count != before)
                ShowStatus(string.Format(Str(found.Count > before ? "Str_GameCtl_Plugged" : "Str_GameCtl_Unplugged"), found.Count), StatusKind.Info);
        }

        private void CmbController_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            SelectController(CmbController.SelectedItem as ControllerInfo);
        }

        private void SelectController(ControllerInfo? info)
        {
            CancelTest();
            ZeroVibration();
            _poller?.Dispose();
            _poller = null;
            _current = info;
            _shownButtons = ulong.MaxValue;
            _wasConnected = false;
            ResetResults();

            bool any = info != null;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            TabBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            ApplyTab();
            PillRate.Visibility = Visibility.Collapsed;

            if (info == null)
            {
                TxtControllerInfo.Text = Str("Str_GameCtl_NoneFound");
                return;
            }

            string battery = info.BatteryPercent >= 0 ? $" · {Str("Str_GameCtl_Battery")} ≈{info.BatteryPercent}%" : string.Empty;
            TxtControllerInfo.Text = $"{info.Detail}\n{string.Format(Str("Str_GameCtl_Capabilities"), info.Axes, info.Buttons)}{battery}";

            bool xinput = info.Api == ControllerApi.XInput;
            VibrationPanel.Visibility = xinput ? Visibility.Visible : Visibility.Collapsed;
            TxtVibrationNote.Visibility = xinput ? Visibility.Collapsed : Visibility.Visible;
            TriggerBars.Visibility = xinput ? Visibility.Visible : Visibility.Collapsed;
            RadioTabTriggers.IsEnabled = xinput;
            if (!xinput && RadioTabTriggers.IsChecked == true) RadioTabLive.IsChecked = true;
            bool hasRightStick = info.Axes >= 4;
            PlotRight.Visibility = TxtRightValues.Visibility = TxtRightTitle.Visibility = hasRightStick ? Visibility.Visible : Visibility.Hidden;
            TxtLeftTitle.Text = Str(xinput ? "Str_GameCtl_LeftStick" : "Str_GameCtl_AxesXY");
            TxtRightTitle.Text = Str(xinput ? "Str_GameCtl_RightStick" : "Str_GameCtl_AxesZR");

            BuildButtons(info);
            BuildExtraAxes(info);

            if (IsVisible)
            {
                _poller = new ControllerPoller(info);
                _uiTimer.Start();
            }
        }

        private void BuildButtons(ControllerInfo info)
        {
            ButtonsPanel.Children.Clear();
            _buttonChips.Clear();

            IEnumerable<(int Bit, string Label)> list = info.Api == ControllerApi.XInput
                ? GameControllerManager.XInputButtons
                : Enumerable.Range(0, Math.Clamp(info.Buttons, 0, 32)).Select(i => (i, (i + 1).ToString(CultureInfo.InvariantCulture)));

            foreach (var (bit, label) in list)
            {
                var text = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                var chip = new Border { MinWidth = 40, Height = 32, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(8), Child = text };
                chip.SetResourceReference(Border.BackgroundProperty, "RowBg");
                ButtonsPanel.Children.Add(chip);
                _buttonChips.Add((bit, chip, text));
            }
        }

        private void BuildExtraAxes(ControllerInfo info)
        {
            ExtraAxesRows.Children.Clear();
            _axisRows.Clear();
            bool generic = info.Api == ControllerApi.Joystick;
            ExtraAxesPanel.Visibility = generic ? Visibility.Visible : Visibility.Collapsed;
            if (!generic) return;

            for (int i = 0; i < info.Axes; i++)
            {
                var name = new TextBlock { Text = string.Format(Str("Str_GameCtl_AxisN"), i + 1), Width = 70, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                var bar = new ProgressBar { Minimum = -1, Maximum = 1, Height = 8, VerticalAlignment = VerticalAlignment.Center };
                var value = new TextBlock { Width = 70, Margin = new Thickness(10, 0, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 12 };
                value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

                var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(bar, 1);
                Grid.SetColumn(value, 2);
                grid.Children.Add(name);
                grid.Children.Add(bar);
                grid.Children.Add(value);
                ExtraAxesRows.Children.Add(grid);
                _axisRows.Add((bar, value));
            }
        }

        // =================================================================
        //  Tabs
        // =================================================================

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            CancelTest();
            ApplyTab();
        }

        private void ApplyTab()
        {
            bool any = _current != null;
            PanelLive.Visibility = any && RadioTabLive.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelSticks.Visibility = any && RadioTabSticks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelTriggers.Visibility = any && RadioTabTriggers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            bool right = _current is { Axes: >= 4 };
            PlotRightTest.Visibility = right ? Visibility.Visible : Visibility.Hidden;
            BtnRestRight.IsEnabled = BtnRangeRight.IsEnabled = right;
            foreach (var panel in new FrameworkElement[] { PanelLive, PanelSticks, PanelTriggers })
                if (panel.Visibility == Visibility.Visible) UiMotion.FadeIn(panel);
        }

        // =================================================================
        //  The tick: read, show, run tests
        // =================================================================

        private void Tick()
        {
            if (_poller == null || _current == null) return;
            var state = _poller.Latest;

            if (!state.Connected)
            {
                if (_wasConnected) ShowStatus(Str("Str_GameCtl_Lost"), StatusKind.Error);
                _wasConnected = false;
                return;
            }
            if (!_wasConnected)
            {
                _wasConnected = true;
                _lastRate = DateTime.MinValue;
            }

            ShowSticks(state);
            ShowTriggers(state);
            ShowAxesAndButtons(state);
            ShowRate();
            RunTest(state);
        }

        private (double X, double Y, double OutX, double OutY, double Mag, double OutMag) StickValues(ControllerState s, int side)
        {
            double x = s.Axis(side * 2), y = s.Axis(side * 2 + 1);
            double dz = (side == 0 ? SliderDzLeft.Value : SliderDzRight.Value) / 100;
            var (ox, oy) = GameControllerManager.ApplyDeadzone(x, y, dz, Mode);
            return (x, y, ox, oy, GameControllerManager.Magnitude(x, y), GameControllerManager.Magnitude(ox, oy));
        }

        private void ShowSticks(ControllerState s)
        {
            var l = StickValues(s, 0);
            var r = StickValues(s, 1);
            string F(double v) => v.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture);

            if (PanelLive.Visibility == Visibility.Visible)
            {
                PlotLeft.SetReading(l.X, l.Y, l.OutX, l.OutY);
                TxtLeftValues.Text = $"X {F(l.X)}  Y {F(l.Y)}\n|{l.Mag:0.00}| → {l.OutMag:0.00}";
                if (_current!.Axes >= 4)
                {
                    PlotRight.SetReading(r.X, r.Y, r.OutX, r.OutY);
                    TxtRightValues.Text = $"X {F(r.X)}  Y {F(r.Y)}\n|{r.Mag:0.00}| → {r.OutMag:0.00}";
                }
            }

            if (PanelSticks.Visibility == Visibility.Visible)
            {
                PlotLeftTest.SetReading(l.X, l.Y, l.OutX, l.OutY);
                if (_current!.Axes >= 4) PlotRightTest.SetReading(r.X, r.Y, r.OutX, r.OutY);
            }
        }

        private void ShowTriggers(ControllerState s)
        {
            if (_current!.Api != ControllerApi.XInput) return;
            double th = SliderDzTrig.Value / 100;

            if (PanelLive.Visibility == Visibility.Visible)
            {
                double lt = GameControllerManager.ApplyTriggerThreshold(s.LeftTrigger, th);
                double rt = GameControllerManager.ApplyTriggerThreshold(s.RightTrigger, th);
                BarLT.Value = lt;
                BarRT.Value = rt;
                TxtLT.Text = $"{Math.Round(s.LeftTrigger * 255),3:0} → {lt * 100,3:0}%";
                TxtRT.Text = $"{Math.Round(s.RightTrigger * 255),3:0} → {rt * 100,3:0}%";
            }

            ChartLT.LiveValue = s.LeftTrigger;
            ChartRT.LiveValue = s.RightTrigger;
        }

        private void ShowAxesAndButtons(ControllerState s)
        {
            if (PanelLive.Visibility != Visibility.Visible) return;

            for (int i = 0; i < _axisRows.Count; i++)
            {
                double v = s.Axis(i);
                _axisRows[i].Bar.Value = v;
                _axisRows[i].Text.Text = v.ToString("+0.000;-0.000;+0.000", CultureInfo.InvariantCulture);
            }

            if (s.Buttons != _shownButtons)
            {
                _shownButtons = s.Buttons;
                foreach (var (bit, chip, text) in _buttonChips)
                {
                    bool down = s.IsDown(bit);
                    chip.SetResourceReference(Border.BackgroundProperty, down ? "AccentFill" : "RowBg");
                    if (down) text.Foreground = Brushes.White;
                    else text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                }
            }

            TxtPov.Text = _current!.HasPov && _current.Api == ControllerApi.Joystick
                ? string.Format(Str("Str_GameCtl_Pov"), s.Pov < 0 ? Str("Str_GameCtl_PovCentered") : $"{s.Pov}°")
                : string.Empty;
        }

        private void ShowRate()
        {
            if ((DateTime.Now - _lastRate).TotalMilliseconds < 500 || _poller == null) return;
            _lastRate = DateTime.Now;
            var hz = _poller.ReportRate;
            if (hz == null) return;
            TxtRate.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_GameCtl_Rate"), GameControllerManager.NearestStandardRate(hz.Value), hz.Value);
            if (PillRate.Visibility != Visibility.Visible) UiMotion.FadeVisibility(PillRate, true);
        }

        // =================================================================
        //  Vibration
        // =================================================================

        private void Motor_SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            UpdateValueTexts();
            if (_current != null) GameControllerManager.SetVibration(_current, SliderMotorLeft.Value / 100, SliderMotorRight.Value / 100);
        }

        private void BtnPulse_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            GameControllerManager.SetVibration(_current, 1, 1);
            var stop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            stop.Tick += (_, _) =>
            {
                stop.Stop();
                if (_current != null) GameControllerManager.SetVibration(_current, SliderMotorLeft.Value / 100, SliderMotorRight.Value / 100);
            };
            stop.Start();
        }

        private void ZeroVibration()
        {
            if (_current != null) GameControllerManager.SetVibration(_current, 0, 0);
            _settingControls = true;
            SliderMotorLeft.Value = SliderMotorRight.Value = 0;
            _settingControls = false;
            if (_viewConstructed) UpdateValueTexts();
        }

        // =================================================================
        //  Tests
        // =================================================================

        private void ResetResults()
        {
            TxtLeftResult.Text = TxtRightResult.Text = TxtLTResult.Text = TxtRTResult.Text = string.Empty;
            BtnUseLeft.Visibility = BtnUseRight.Visibility = Visibility.Collapsed;
            foreach (var plot in new[] { PlotLeftTest, PlotRightTest, PlotLeft, PlotRight })
            {
                plot.Envelope = null;
                plot.RestCenter = null;
                plot.ClearTrail();
            }
            ChartLT.Samples = ChartRT.Samples = null;
            Array.Clear(_liveEnvelope[0]);
            Array.Clear(_liveEnvelope[1]);
            _restResults[0] = _restResults[1] = null;
        }

        private void SetTestButtons(bool running)
        {
            bool right = _current is { Axes: >= 4 };
            BtnRestLeft.IsEnabled = BtnRangeLeft.IsEnabled = !running;
            BtnRestRight.IsEnabled = BtnRangeRight.IsEnabled = !running && right;
            BtnRecLT.IsEnabled = BtnRecRT.IsEnabled = !running;
            RadioTabLive.IsEnabled = RadioTabSticks.IsEnabled = !running;
            RadioTabTriggers.IsEnabled = !running && _current?.Api == ControllerApi.XInput;
        }

        private void CancelTest()
        {
            if (_test == Test.None) return;
            _poller?.StopRecording();
            _test = Test.None;
            SetTestButtons(false);
        }

        private static int Side(object sender) => sender is FrameworkElement { Tag: "R" } ? 1 : 0;

        private void BtnRest_Click(object sender, RoutedEventArgs e) => BeginTest(Test.Rest, Side(sender));

        private void BtnRange_Click(object sender, RoutedEventArgs e) => BeginTest(Test.Range, Side(sender));

        private void BtnRecord_Click(object sender, RoutedEventArgs e) => BeginTest(Test.Trigger, Side(sender));

        private void BeginTest(Test test, int side)
        {
            if (_poller == null || _current == null || _test != Test.None) return;
            if (!_poller.Latest.Connected)
            {
                ShowStatus(Str("Str_GameCtl_Lost"), StatusKind.Error);
                return;
            }

            _test = test;
            _testSide = side;
            _testStart = DateTime.Now;
            _triggerSamples.Clear();
            SetTestButtons(true);
            _poller.StartRecording();

            if (test == Test.Range)
            {
                Array.Clear(_liveEnvelope[side]);
                (side == 0 ? PlotLeftTest : PlotRightTest).Envelope = _liveEnvelope[side];
            }
            if (test == Test.Trigger) (side == 0 ? ChartLT : ChartRT).Samples = _triggerSamples;
            (side == 0 ? BtnUseLeft : BtnUseRight).Visibility = Visibility.Collapsed;
        }

        private void RunTest(ControllerState state)
        {
            if (_test == Test.None) return;
            double elapsed = (DateTime.Now - _testStart).TotalSeconds;
            var resultText = _test == Test.Trigger ? (_testSide == 0 ? TxtLTResult : TxtRTResult) : (_testSide == 0 ? TxtLeftResult : TxtRightResult);

            switch (_test)
            {
                case Test.Rest:
                    resultText.Text = string.Format(Str("Str_GameCtl_RestRunning"), Math.Max(0, RestSeconds - elapsed));
                    if (elapsed >= RestSeconds) FinishRest();
                    break;

                case Test.Range:
                    GameControllerManager.AddRangeSample(_liveEnvelope[_testSide], state.Axis(_testSide * 2), state.Axis(_testSide * 2 + 1));
                    (_testSide == 0 ? PlotLeftTest : PlotRightTest).Envelope = (double[])_liveEnvelope[_testSide].Clone();
                    resultText.Text = string.Format(Str("Str_GameCtl_RangeRunning"), Math.Max(0, RangeSeconds - elapsed));
                    if (elapsed >= RangeSeconds) FinishRange();
                    break;

                case Test.Trigger:
                {
                    double v = _testSide == 0 ? state.LeftTrigger : state.RightTrigger;
                    _triggerSamples.Add((elapsed, v));
                    (_testSide == 0 ? ChartLT : ChartRT).InvalidateVisual();
                    resultText.Text = Str("Str_GameCtl_TriggerRunning");
                    bool pulled = _triggerSamples.Any(s => s.Value > 0.5);
                    if (elapsed >= TriggerMaxSeconds || (pulled && v < 0.03 && elapsed > 0.5)) FinishTrigger();
                    break;
                }
            }
        }

        private void FinishRest()
        {
            int side = _testSide;
            var recorded = _poller?.StopRecording() ?? new List<TimedControllerState>();
            _test = Test.None;
            SetTestButtons(false);

            var samples = recorded.Select(r => (X: r.State.Axis(side * 2), Y: r.State.Axis(side * 2 + 1))).ToList();
            var result = GameControllerManager.AnalyzeRest(samples);
            var text = side == 0 ? TxtLeftResult : TxtRightResult;
            if (result == null)
            {
                text.Text = Str("Str_GameCtl_NoData");
                return;
            }

            _restResults[side] = result;
            (side == 0 ? PlotLeftTest : PlotRightTest).RestCenter = new Point(result.CenterX, result.CenterY);
            double current = (side == 0 ? SliderDzLeft.Value : SliderDzRight.Value) / 100;
            string verdict = result.MaxRadius <= 0.003
                ? Str("Str_GameCtl_RestPerfect")
                : result.MaxRadius * 1.0 <= current
                    ? string.Format(Str("Str_GameCtl_RestCovered"), (int)(current * 100))
                    : string.Format(Str("Str_GameCtl_RestDrifts"), (int)(current * 100));
            text.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_GameCtl_RestResult"),
                result.CenterX.ToString("+0.000;-0.000;+0.000", CultureInfo.InvariantCulture),
                result.CenterY.ToString("+0.000;-0.000;+0.000", CultureInfo.InvariantCulture),
                result.MaxRadius * 100, result.SuggestedDeadzonePercent) + "\n" + verdict;

            var use = side == 0 ? BtnUseLeft : BtnUseRight;
            (side == 0 ? TxtUseLeft : TxtUseRight).Text = string.Format(Str("Str_GameCtl_UseSuggested"), result.SuggestedDeadzonePercent);
            use.Visibility = Visibility.Visible;
            UiMotion.Pop(use);
        }

        private void FinishRange()
        {
            int side = _testSide;
            var recorded = _poller?.StopRecording() ?? new List<TimedControllerState>();
            _test = Test.None;
            SetTestButtons(false);

            var envelope = new double[GameControllerManager.EnvelopeBuckets];
            foreach (var r in recorded) GameControllerManager.AddRangeSample(envelope, r.State.Axis(side * 2), r.State.Axis(side * 2 + 1));
            var result = GameControllerManager.AnalyzeRange(envelope, recorded.Count);
            var text = side == 0 ? TxtLeftResult : TxtRightResult;
            if (result == null)
            {
                text.Text = Str("Str_GameCtl_RangeNone");
                (side == 0 ? PlotLeftTest : PlotRightTest).Envelope = null;
                return;
            }

            (side == 0 ? PlotLeftTest : PlotRightTest).Envelope = result.Envelope;
            string shape = result.Roundness >= 0.97 ? Str("Str_GameCtl_ShapeRound")
                : result.Roundness >= 0.88 ? Str("Str_GameCtl_ShapeOctagon")
                : Str("Str_GameCtl_ShapeUneven");
            string reach = result.MaxRadius < 0.93 ? Str("Str_GameCtl_ReachShort") : string.Empty;
            string coverage = result.Coverage < 0.9 ? Str("Str_GameCtl_CoverageLow") : string.Empty;
            text.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_GameCtl_RangeResult"), result.MaxRadius * 100, result.Roundness * 100, result.Coverage * 100)
                        + "\n" + shape + (reach.Length > 0 ? "\n" + reach : string.Empty) + (coverage.Length > 0 ? "\n" + coverage : string.Empty);
        }

        private void FinishTrigger()
        {
            int side = _testSide;
            _poller?.StopRecording();
            _test = Test.None;
            SetTestButtons(false);

            var result = GameControllerManager.AnalyzeTrigger(_triggerSamples.ToList());
            var text = side == 0 ? TxtLTResult : TxtRTResult;
            if (result == null)
            {
                text.Text = Str("Str_GameCtl_TriggerNone");
                return;
            }

            string linear = result.Linearity >= 0.9 ? Str("Str_GameCtl_TriggerEven") : Str("Str_GameCtl_TriggerUneven");
            string reach = result.Maximum < 0.98 ? Str("Str_GameCtl_TriggerShort") : string.Empty;
            text.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_GameCtl_TriggerResult"), result.FirstActivation * 255, result.Maximum * 255, result.Linearity * 100)
                        + "\n" + linear + (reach.Length > 0 ? "\n" + reach : string.Empty);
        }

        private void BtnUseSuggested_Click(object sender, RoutedEventArgs e)
        {
            int side = Side(sender);
            var result = _restResults[side];
            if (result == null) return;
            (side == 0 ? SliderDzLeft : SliderDzRight).Value = Math.Clamp(result.SuggestedDeadzonePercent, 0, 50);
            ShowStatus(string.Format(Str("Str_GameCtl_DeadzoneSet"), result.SuggestedDeadzonePercent), StatusKind.Success);
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
            _statusTimer.Interval = TimeSpan.FromSeconds(kind == StatusKind.Error ? 8 : 4);
            _statusTimer.Start();
        }

        private string Str(string key) => TryFindResource(key) as string ?? key;
    }
}
