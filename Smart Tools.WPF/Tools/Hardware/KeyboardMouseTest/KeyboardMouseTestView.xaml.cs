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
using System.Windows.Shapes;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Keyboard &amp; Mouse Event Tester - UI only. Devices are read through <see cref="RawInputListener"/>; keys, rollover, chatter,
    /// polling rate and DPI maths are in <see cref="KeyboardMouseTestManager"/>. Nothing is recorded while the page isn't showing,
    /// and only input that goes to this program is read.
    /// </summary>
    public partial class KeyboardMouseTestView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const double Unit = 40;
        private const int LogLines = 12;
        private const int ReactionHistory = 10;

        private enum StatusKind { Success, Error, Info }
        private enum ReactionState { Idle, Waiting, Go, TooEarly, Done }
        private enum Tab { Keyboard, Mouse, Reaction }

        private sealed class KeyTile
        {
            public required Border Frame;
            public required TextBlock Text;
        }

        private sealed class ButtonRow
        {
            public required MouseButtonKind Button;
            public required Border Visual;
            public required TextBlock Clicks, Shortest, Chatter;
            public int Count;
            public bool Down;
        }

        private readonly KeyboardMouseTestSettings _settings;
        private readonly DispatcherTimer _pump;
        private readonly DispatcherTimer _slowTimer;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _reactionTimer;
        private readonly bool _viewConstructed;

        private readonly Dictionary<string, KeyTile> _tiles = new();
        private readonly KeyboardMouseTestManager.RolloverTracker _rollover = new();
        private readonly KeyboardMouseTestManager.ChatterDetector _keyChatter = new();
        private readonly KeyboardMouseTestManager.ChatterDetector _mouseChatter = new();
        private readonly KeyboardMouseTestManager.WheelAnalyzer _wheel = new();
        private readonly Dictionary<string, double> _keyDownAt = new();
        private readonly List<string> _log = new();
        private readonly List<ButtonRow> _rows = new();
        private readonly List<ReactionResult> _reactions = new();
        private readonly List<Border> _histBars = new();
        private readonly Random _random = new();

        // movement indicator
        private const double MotionCenter = 55, MotionRing = 52, MotionTravel = 40;
        private readonly KeyboardMouseTestManager.MotionTracker _motion = new();
        private readonly List<Ellipse> _trailDots = new();
        private Ellipse? _puck;
        private Path? _chevron;
        private RotateTransform? _chevronTurn;
        private double _lastMotionAt;
        private bool _motionAtRest = true;

        private RawInputListener? _listener;
        private PollingResult? _lastPolling;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _settingControls;
        private Window? _hostWindow;

        // DPI measurement
        private bool _measuringDpi;

        // reaction test
        private ReactionState _reaction = ReactionState.Idle;
        private double _waitStarted, _goTime;

        public KeyboardMouseTestView()
        {
            InitializeComponent();

            _settings = KeyboardMouseTestManager.LoadSettings();

            _pump = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(8) };
            _pump.Tick += (_, _) => Pump();

            _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _slowTimer.Tick += (_, _) => SlowTick();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                UiMotion.FadeVisibility(BorderStatus, false);
            };

            _reactionTimer = new DispatcherTimer();
            _reactionTimer.Tick += (_, _) =>
            {
                _reactionTimer.Stop();
                GoSignal();
            };

            _settingControls = true;
            SliderChatter.Value = _settings.ChatterMs;
            SliderDpiCm.Value = _settings.DpiDistanceCm;
            SliderMotionRange.Value = _settings.MotionRange;
            ChkBlockKeys.IsChecked = _settings.BlockKeys;
            _settingControls = false;
            if (_settings.SidebarCollapsed) SetSidebarCollapsed(true);

            _viewConstructed = true;
            BuildKeyboard();
            BuildButtonsTable();
            BuildMotionIndicator();
            ApplyChatterLimit();
            UpdateValueTexts();
            UpdateKeyboardPills();
            UpdateButtonsTable();
            ApplyTab();
            ResetDpiUi();
            ShowReaction(ReactionState.Idle);
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            StopListening();
            _statusTimer.Stop();
            _reactionTimer.Stop();
            SaveSettings();
        }

        private Tab CurrentTab => RadioTabMouse.IsChecked == true ? Tab.Mouse : RadioTabReaction.IsChecked == true ? Tab.Reaction : Tab.Keyboard;

        // =================================================================
        //  Starting and stopping
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                _hostWindow = Window.GetWindow(this);
                if (_hostWindow != null) _hostWindow.Deactivated += (_, _) => ReleaseEverything();
            }
            if (IsVisible) StartListening();
        }

        private void Root_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!_loadedOnce) return;
            if (IsVisible) StartListening();
            else StopListening();
        }

        private void StartListening()
        {
            if (_listener == null)
            {
                try
                {
                    _listener = new RawInputListener();
                }
                catch (InvalidOperationException ex)
                {
                    ShowStatus(string.Format(Str("Str_KbMouse_ListenFailed"), ex.Message), StatusKind.Error);
                    return;
                }
            }
            _listener.Enabled = true;
            _pump.Start();
            _slowTimer.Start();
            Focus();
        }

        /// <summary>Leaving the page: nothing is read and no timer runs.</summary>
        private void StopListening()
        {
            _pump.Stop();
            _slowTimer.Stop();
            _reactionTimer.Stop();
            if (_reaction is ReactionState.Waiting or ReactionState.Go) ShowReaction(ReactionState.Idle);
            _measuringDpi = false;
            ResetDpiUi();
            ResetMotion();
            ReleaseEverything();
            _listener?.Dispose();
            _listener = null;
        }

        /// <summary>The window lost the keyboard (Alt+Tab, a dialog): a key released over there would never reach us, so nothing stays "held".</summary>
        private void ReleaseEverything()
        {
            foreach (var id in _rollover.Held.ToList())
            {
                _rollover.Release(id);
                RefreshTile(id);
            }
            foreach (var row in _rows)
            {
                row.Down = false;
                PaintButton(row);
            }
            UpdateKeyboardPills();
        }

        // =================================================================
        //  Settings
        // =================================================================

        private void SaveSettings()
        {
            if (!_viewConstructed) return;
            try
            {
                _settings.ChatterMs = (int)SliderChatter.Value;
                _settings.DpiDistanceCm = (int)SliderDpiCm.Value;
                _settings.MotionRange = (int)SliderMotionRange.Value;
                _settings.BlockKeys = ChkBlockKeys.IsChecked == true;
                _settings.SidebarCollapsed = _optionsCollapsed;
                KeyboardMouseTestManager.SaveSettings(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Keyboard & mouse test settings: {ex.Message}");
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

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed || _settingControls) return;
            UpdateValueTexts();
            ApplyChatterLimit();
            if (_measuringDpi) UpdateDpiInstruction();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _settingControls) return;
            _settings.BlockKeys = ChkBlockKeys.IsChecked == true;
        }

        private void UpdateValueTexts()
        {
            TxtChatterLimit.Text = $"{(int)SliderChatter.Value} ms";
            TxtDpiDistance.Text = $"{(int)SliderDpiCm.Value} cm";
            TxtMotionRange.Text = $"{(int)SliderMotionRange.Value}";
            _motion.FullScale = SliderMotionRange.Value;
        }

        private void ApplyChatterLimit()
        {
            _keyChatter.ThresholdMs = SliderChatter.Value;
            _mouseChatter.ThresholdMs = SliderChatter.Value;
        }

        // =================================================================
        //  Tabs and focus
        // =================================================================

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            ApplyTab();
            Focus();
        }

        private void ApplyTab()
        {
            var tab = CurrentTab;
            PanelKeyboard.Visibility = tab == Tab.Keyboard ? Visibility.Visible : Visibility.Collapsed;
            PanelMouse.Visibility = tab == Tab.Mouse ? Visibility.Visible : Visibility.Collapsed;
            PanelReaction.Visibility = tab == Tab.Reaction ? Visibility.Visible : Visibility.Collapsed;
            PanelDpiOption.Visibility = tab == Tab.Mouse ? Visibility.Visible : Visibility.Collapsed;
            PanelMotionOption.Visibility = tab == Tab.Mouse ? Visibility.Visible : Visibility.Collapsed;
            UpdateFocusHint();
            foreach (var panel in new FrameworkElement[] { PanelKeyboard, PanelMouse, PanelReaction })
                if (panel.Visibility == Visibility.Visible) UiMotion.FadeIn(panel);
            if (tab == Tab.Mouse) DrawHistogram();
        }

        private void Root_FocusChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateFocusHint();

        private void UpdateFocusHint()
        {
            if (!_viewConstructed) return;
            BorderFocusHint.Visibility = CurrentTab == Tab.Keyboard && !IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsKeyboardFocusWithin) Focus();
        }

        /// <summary>
        /// While a test is on screen the keys belong to the test: without this Space would press the focused button,
        /// Tab would move focus and Alt would open a menu, and the typing would reach the rest of the program.
        /// </summary>
        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ShouldBlockKeys()) e.Handled = true;
        }

        private void Root_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (ShouldBlockKeys()) e.Handled = true;
        }

        private bool ShouldBlockKeys() =>
            IsVisible && ChkBlockKeys.IsChecked == true && CurrentTab != Tab.Mouse && Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or Slider);

        // =================================================================
        //  Draining the device queues
        // =================================================================

        private void Pump()
        {
            var listener = _listener;
            if (listener == null) return;

            while (listener.KeyEvents.TryDequeue(out var key)) HandleKey(key);
            while (listener.ButtonEvents.TryDequeue(out var mouse)) HandleMouse(mouse);
            StepMotion(listener);
        }

        // ---- movement indicator ------------------------------------------------------

        private void BuildMotionIndicator()
        {
            MotionCanvas.Children.Clear();
            _trailDots.Clear();

            var ring = new Ellipse { Width = MotionRing * 2, Height = MotionRing * 2, StrokeThickness = 2 };
            ring.SetResourceReference(Shape.FillProperty, "ContentBg");
            ring.SetResourceReference(Shape.StrokeProperty, "CardBorder");
            Canvas.SetLeft(ring, MotionCenter - MotionRing);
            Canvas.SetTop(ring, MotionCenter - MotionRing);
            MotionCanvas.Children.Add(ring);

            foreach (var (x1, y1, x2, y2) in new[]
            {
                (MotionCenter - MotionRing, MotionCenter, MotionCenter + MotionRing, MotionCenter),
                (MotionCenter, MotionCenter - MotionRing, MotionCenter, MotionCenter + MotionRing),
            })
            {
                var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 } };
                line.SetResourceReference(Shape.StrokeProperty, "CardBorder");
                MotionCanvas.Children.Add(line);
            }

            for (int i = 0; i < KeyboardMouseTestManager.MotionTracker.TrailLength; i++)
            {
                double size = 3 + 6.0 * i / KeyboardMouseTestManager.MotionTracker.TrailLength;
                var dot = new Ellipse { Width = size, Height = size, Opacity = 0, IsHitTestVisible = false };
                dot.SetResourceReference(Shape.FillProperty, "AccentFill");
                MotionCanvas.Children.Add(dot);
                _trailDots.Add(dot);
            }

            _puck = new Ellipse { Width = 24, Height = 24 };
            _puck.SetResourceReference(Shape.FillProperty, "AccentFill");
            MotionCanvas.Children.Add(_puck);

            // The chevron points up before it is turned; its geometry is centred on its own origin so the turn is about the puck.
            _chevronTurn = new RotateTransform();
            _chevron = new Path
            {
                Data = Geometry.Parse("M -5.5,3 L 0,-4 L 5.5,3"),
                Stroke = Brushes.White,
                StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                RenderTransform = _chevronTurn,
                IsHitTestVisible = false,
            };
            MotionCanvas.Children.Add(_chevron);
            RenderMotion();
        }

        private void StepMotion(RawInputListener listener)
        {
            double now = listener.Now;
            double dt = now - _lastMotionAt;
            _lastMotionAt = now;
            var (dx, dy) = listener.TakeFrameDelta();
            if (CurrentTab != Tab.Mouse) return;

            bool active = dx != 0 || dy != 0 || _motion.Speed >= 1;
            if (!active && _motionAtRest) return;
            _motionAtRest = !active;

            _motion.Step(dx, dy, dt);
            RenderMotion();
        }

        private void RenderMotion()
        {
            if (_puck == null || _chevron == null || _chevronTurn == null) return;

            var (px, py) = _motion.Puck;
            double cx = MotionCenter + px * MotionTravel, cy = MotionCenter + py * MotionTravel;
            Canvas.SetLeft(_puck, cx - _puck.Width / 2);
            Canvas.SetTop(_puck, cy - _puck.Height / 2);
            Canvas.SetLeft(_chevron, cx);
            Canvas.SetTop(_chevron, cy);
            _chevronTurn.Angle = _motion.Angle + 90;
            _chevron.Opacity = _motion.Moving ? 1 : 0.35;

            int n = _trailDots.Count, have = _motion.Trail.Count, i = 0;
            foreach (var (tx, ty) in _motion.Trail)
            {
                var dot = _trailDots[n - have + i];
                Canvas.SetLeft(dot, MotionCenter + tx * MotionTravel - dot.Width / 2);
                Canvas.SetTop(dot, MotionCenter + ty * MotionTravel - dot.Height / 2);
                dot.Opacity = 0.45 * (i + 1) / have;
                i++;
            }
            for (int k = 0; k < n - have; k++) _trailDots[k].Opacity = 0;
        }

        private void ResetMotion()
        {
            _motion.Reset();
            _motionAtRest = true;
            RenderMotion();
            TxtMotionArrow.Text = "—";
            TxtMotionStats.Text = string.Empty;
        }

        private void UpdateMotionText()
        {
            TxtMotionArrow.Text = _motion.Moving ? KeyboardMouseTestManager.CompassArrow(_motion.Angle) : "—";
            TxtMotionStats.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_MotionStats"), _motion.Speed, _motion.Moving ? _motion.Angle : 0, _motion.Deflection * 100);
        }

        // ---- keyboard ----------------------------------------------------------------

        private void HandleKey(RawKeyEvent e)
        {
            string label = KeyboardMouseTestManager.FullLabelOf(e.Id);

            if (e.Down)
            {
                if (!_rollover.Press(e.Id)) return;   // auto-repeat
                _keyDownAt[e.Id] = e.Time;

                var chatter = _keyChatter.Press(e.Id, e.Time);
                AddLog($"↓ {label,-12} {e.Id,-6}" + (chatter != null ? $"  ⚠ {string.Format(Str("Str_KbMouse_ChatterShort"), chatter.GapMs)}" : string.Empty));
                OnReactionInput(e.Time, Str("Str_KbMouse_DeviceKeyboard"));
                if (_measuringDpi && e.Id is "39" or "1C" or "E0-1C") FinishDpi();
            }
            else
            {
                if (!_rollover.Release(e.Id)) return;
                _keyChatter.Release(e.Id, e.Time);
                string hold = _keyDownAt.TryGetValue(e.Id, out double down) ? $"  {Str("Str_KbMouse_Held")} {(e.Time - down) * 1000:0} ms" : string.Empty;
                AddLog($"↑ {label,-12} {e.Id,-6}{hold}");
            }

            RefreshTile(e.Id);
            UpdateKeyboardPills();
        }

        private void BuildKeyboard()
        {
            KeysCanvas.Width = KeyboardMouseTestManager.LayoutSize.Width * Unit;
            KeysCanvas.Height = KeyboardMouseTestManager.LayoutSize.Height * Unit;
            foreach (var key in KeyboardMouseTestManager.Layout)
            {
                var text = new TextBlock
                {
                    Text = key.Label,
                    FontSize = key.Label.Length <= 1 ? 14 : key.Label.Length <= 3 ? 11 : 10,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var frame = new Border
                {
                    Width = key.Width * Unit - 4,
                    Height = key.Height * Unit - 4,
                    CornerRadius = new CornerRadius(6),
                    Child = text,
                    ToolTip = KeyboardMouseTestManager.FullLabelOf(key.Id),
                };
                Canvas.SetLeft(frame, key.X * Unit + 2);
                Canvas.SetTop(frame, key.Y * Unit + 2);
                KeysCanvas.Children.Add(frame);
                var tile = new KeyTile { Frame = frame, Text = text };
                _tiles[key.Id] = tile;
                PaintTile(key.Id, tile);
            }
        }

        private void RefreshTile(string id)
        {
            if (_tiles.TryGetValue(id, out var tile)) PaintTile(id, tile);
        }

        private void PaintTile(string id, KeyTile tile)
        {
            if (_rollover.Held.Contains(id))
            {
                tile.Frame.SetResourceReference(Border.BackgroundProperty, "AccentFill");
                tile.Text.Foreground = Brushes.White;
            }
            else if (_rollover.Tested.Contains(id))
            {
                tile.Frame.SetResourceReference(Border.BackgroundProperty, "StatusSuccessBg");
                tile.Text.SetResourceReference(TextBlock.ForegroundProperty, "StatusSuccessFg");
            }
            else
            {
                tile.Frame.SetResourceReference(Border.BackgroundProperty, "RowBg");
                tile.Text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            }
        }

        private void UpdateKeyboardPills()
        {
            int tested = _rollover.Tested.Count(id => _tiles.ContainsKey(id));
            TxtPillTested.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_Tested"), tested, KeyboardMouseTestManager.KeyCount);
            TxtPillHeld.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_HeldNow"), _rollover.Held.Count);
            TxtPillMax.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_MaxAtOnce"), _rollover.MaxHeld);

            string verdict = KeyboardMouseTestManager.RolloverVerdictKey(_rollover.MaxHeld);
            bool good = verdict is "RolloverFull" or "RolloverMore";
            bool warn = verdict is "Rollover6" or "RolloverFew";
            PillMax.SetResourceReference(Border.BackgroundProperty, good ? "StatusSuccessBg" : warn ? "StatusWarningBg" : "RowBg");
            TxtPillMax.SetResourceReference(TextBlock.ForegroundProperty, good ? "StatusSuccessFg" : warn ? "StatusWarningFg" : "TextSecondary");
            TxtRolloverVerdict.Text = Str("Str_KbMouse_" + verdict);
            TxtMaxSet.Text = _rollover.MaxHeld == 0 ? string.Empty : string.Join(" + ", _rollover.MaxSet.Select(KeyboardMouseTestManager.LabelOf));

            int chatter = _keyChatter.Total;
            TxtPillChatter.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_ChatterCount"), chatter);
            PillChatter.SetResourceReference(Border.BackgroundProperty, chatter > 0 ? "StatusErrorBg" : "RowBg");
            TxtPillChatter.SetResourceReference(TextBlock.ForegroundProperty, chatter > 0 ? "StatusErrorFg" : "TextSecondary");
            TxtChatterKeys.Text = chatter == 0
                ? string.Empty
                : string.Format(Str("Str_KbMouse_ChatterKeys"), string.Join(", ", _keyChatter.Counts.OrderByDescending(p => p.Value).Select(p => $"{KeyboardMouseTestManager.FullLabelOf(p.Key)} ×{p.Value}")), (int)SliderChatter.Value);
        }

        private void AddLog(string line)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
            if (_log.Count > LogLines) _log.RemoveRange(0, _log.Count - LogLines);
            LogList.ItemsSource = null;
            LogList.ItemsSource = _log.AsEnumerable().Reverse().ToList();
            TxtLogEmpty.Visibility = Visibility.Collapsed;
        }

        // ---- mouse -------------------------------------------------------------------

        private void BuildButtonsTable()
        {
            ButtonsTable.Children.Clear();
            _rows.Clear();

            ButtonsTable.Children.Add(TableRow(Str("Str_KbMouse_ColButton"), Str("Str_KbMouse_ColClicks"), Str("Str_KbMouse_ColShortest"), Str("Str_KbMouse_ColChatter"), header: true, out _, out _, out _));

            var visuals = new Dictionary<MouseButtonKind, Border>
            {
                [MouseButtonKind.Left] = MouseLeft, [MouseButtonKind.Right] = MouseRight, [MouseButtonKind.Middle] = MouseMiddle,
                [MouseButtonKind.Back] = MouseBack, [MouseButtonKind.Forward] = MouseForward,
            };
            foreach (var button in Enum.GetValues<MouseButtonKind>())
            {
                var grid = TableRow(Str("Str_KbMouse_Btn" + button), "0", "—", "0", header: false, out var clicks, out var shortest, out var chatter);
                ButtonsTable.Children.Add(grid);
                _rows.Add(new ButtonRow { Button = button, Visual = visuals[button], Clicks = clicks, Shortest = shortest, Chatter = chatter });
            }
        }

        private static string ButtonId(MouseButtonKind b) => "M" + (int)b;

        private Grid TableRow(string name, string clicks, string shortest, string chatter, bool header, out TextBlock tClicks, out TextBlock tShortest, out TextBlock tChatter)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            TextBlock Cell(string text, int col)
            {
                var t = new TextBlock { Text = text, FontSize = 12, FontFamily = header ? new FontFamily("Segoe UI") : new FontFamily("Consolas"), FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal };
                t.SetResourceReference(TextBlock.ForegroundProperty, header ? "TextSecondary" : "TextPrimary");
                Grid.SetColumn(t, col);
                grid.Children.Add(t);
                return t;
            }
            Cell(name, 0);
            tClicks = Cell(clicks, 1);
            tShortest = Cell(shortest, 2);
            tChatter = Cell(chatter, 3);
            return grid;
        }

        private void HandleMouse(RawMouseEvent e)
        {
            void Press(MouseButtonFlags down, MouseButtonFlags up, MouseButtonKind button)
            {
                var row = _rows[(int)button];
                if ((e.Buttons & down) != 0)
                {
                    row.Down = true;
                    row.Count++;
                    var chatter = _mouseChatter.Press(ButtonId(button), e.Time);
                    UpdateRow(row, chatter != null);
                    OnReactionInput(e.Time, Str("Str_KbMouse_DeviceMouse"));
                    PaintButton(row);
                }
                if ((e.Buttons & up) != 0)
                {
                    row.Down = false;
                    _mouseChatter.Release(ButtonId(button), e.Time);
                    PaintButton(row);
                }
            }

            Press(MouseButtonFlags.LeftDown, MouseButtonFlags.LeftUp, MouseButtonKind.Left);
            Press(MouseButtonFlags.RightDown, MouseButtonFlags.RightUp, MouseButtonKind.Right);
            Press(MouseButtonFlags.MiddleDown, MouseButtonFlags.MiddleUp, MouseButtonKind.Middle);
            Press(MouseButtonFlags.Button4Down, MouseButtonFlags.Button4Up, MouseButtonKind.Back);
            Press(MouseButtonFlags.Button5Down, MouseButtonFlags.Button5Up, MouseButtonKind.Forward);

            if ((e.Buttons & MouseButtonFlags.Wheel) != 0 && e.WheelDelta != 0)
            {
                _wheel.Add(e.WheelDelta, e.Time);
                FlashWheel(e.WheelDelta > 0);
                UpdateWheelText();
            }
        }

        private void UpdateRow(ButtonRow row, bool wasChatter)
        {
            string id = ButtonId(row.Button);
            row.Clicks.Text = row.Count.ToString(CultureInfo.InvariantCulture);
            double? shortest = _mouseChatter.ShortestGapMs(id);
            row.Shortest.Text = shortest == null ? "—" : $"{shortest.Value:0} ms";
            int chatter = _mouseChatter.Counts.GetValueOrDefault(id);
            row.Chatter.Text = chatter.ToString(CultureInfo.InvariantCulture);
            row.Chatter.SetResourceReference(TextBlock.ForegroundProperty, chatter > 0 ? "StatusErrorFg" : "TextPrimary");
            if (wasChatter) UiMotion.Pop(row.Chatter);

            int total = _mouseChatter.Total;
            TxtMouseChatter.Text = total == 0 ? string.Empty : string.Format(Str("Str_KbMouse_MouseChatter"), total, (int)SliderChatter.Value);
        }

        private void UpdateButtonsTable()
        {
            foreach (var row in _rows) UpdateRow(row, wasChatter: false);
            UpdateWheelText();
        }

        private void PaintButton(ButtonRow row)
        {
            if (row.Down) row.Visual.SetResourceReference(Border.BackgroundProperty, "AccentFill");
            else row.Visual.SetResourceReference(Border.BackgroundProperty, row.Count > 0 ? "StatusSuccessBg" : "ContentBg");
        }

        private void FlashWheel(bool up)
        {
            var mark = up ? WheelUpMark : WheelDownMark;
            mark.SetResourceReference(Border.BackgroundProperty, "AccentFill");
            var other = up ? WheelDownMark : WheelUpMark;
            other.SetResourceReference(Border.BackgroundProperty, "ContentBg");
        }

        private void UpdateWheelText()
        {
            TxtWheel.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_WheelText"), _wheel.Up, _wheel.Down, _wheel.Reversals);
            TxtWheel.SetResourceReference(TextBlock.ForegroundProperty, _wheel.Reversals > 0 ? "StatusErrorFg" : "TextPrimary");
        }

        // =================================================================
        //  Slow tick: polling rate, movement, DPI
        // =================================================================

        private void SlowTick()
        {
            var listener = _listener;
            if (listener == null || CurrentTab != Tab.Mouse) return;
            UpdateMotionText();

            double now = listener.Now;
            long counts = listener.TakeMovedCounts();
            double perSecond = counts / _slowTimer.Interval.TotalSeconds;

            var recent = listener.SnapshotMoveTimes().Where(t => now - t < 4.0).ToList();
            var result = KeyboardMouseTestManager.AnalyzePolling(recent);
            if (result != null)
            {
                _lastPolling = result;
                ShowPolling(result);
            }
            TxtPollStats.Text = (_lastPolling == null
                ? string.Empty
                : string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_PollStats"), _lastPolling.MedianMs, _lastPolling.MinMs, _lastPolling.MaxMs, _lastPolling.JitterMs, _lastPolling.LatePercent, _lastPolling.Reports) + "\n")
                + string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_Movement"), perSecond);

            if (_measuringDpi)
            {
                var (dx, dy) = listener.ReadDistance();
                TxtDpiLive.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_DpiLive"), KeyboardMouseTestManager.NetDistance(dx, dy));
            }
        }

        private void ShowPolling(PollingResult r)
        {
            int standard = KeyboardMouseTestManager.NearestStandardRate(r.Hz);
            TxtHz.Text = string.Format(CultureInfo.CurrentCulture, "{0:0} Hz", r.Hz);
            TxtHzNote.Text = Math.Abs(r.Hz - standard) <= standard * 0.08
                ? string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_PollingStandard"), standard)
                : Str("Str_KbMouse_PollingOdd");
            bool steady = r.LatePercent < 3 && r.JitterMs < Math.Max(0.15, r.MedianMs * 0.35);
            TxtPollVerdict.Text = Str(steady ? "Str_KbMouse_PollingSteady" : "Str_KbMouse_PollingUneven");
            DrawHistogram();
        }

        private void DrawHistogram()
        {
            HistCanvas.Children.Clear();
            var r = _lastPolling;
            if (r == null || HistCanvas.ActualWidth < 50) return;

            int n = KeyboardMouseTestManager.HistogramBuckets;
            double w = HistCanvas.ActualWidth / n, h = HistCanvas.ActualHeight - 16;
            int max = Math.Max(1, r.Histogram.Max());
            for (int i = 0; i < n; i++)
            {
                double bar = h * r.Histogram[i] / max;
                var rect = new Border { Width = Math.Max(2, w - 4), Height = Math.Max(r.Histogram[i] > 0 ? 2 : 0, bar), CornerRadius = new CornerRadius(3) };
                rect.SetResourceReference(Border.BackgroundProperty, "AccentFill");
                Canvas.SetLeft(rect, i * w + 2);
                Canvas.SetTop(rect, h - rect.Height);
                HistCanvas.Children.Add(rect);

                var label = new TextBlock { Text = i == n - 1 ? $"≥{KeyboardMouseTestManager.HistogramEdgesMs[i - 1]:0.##}" : $"≤{KeyboardMouseTestManager.HistogramEdgesMs[i]:0.##}", FontSize = 9 };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                Canvas.SetLeft(label, i * w + 1);
                Canvas.SetTop(label, h + 2);
                HistCanvas.Children.Add(label);
            }
        }

        private void BtnResetPolling_Click(object sender, RoutedEventArgs e)
        {
            _listener?.ClearMoveTimes();
            ResetPollingUi();
        }

        private void ResetPollingUi()
        {
            _lastPolling = null;
            TxtHz.Text = "—";
            TxtHzNote.Text = Str("Str_KbMouse_PollingIdle");
            TxtPollStats.Text = string.Empty;
            TxtPollVerdict.Text = string.Empty;
            HistCanvas.Children.Clear();
        }

        // ---- DPI ---------------------------------------------------------------------

        private void BtnDpi_Click(object sender, RoutedEventArgs e)
        {
            if (_listener == null) return;
            if (_measuringDpi)
            {
                FinishDpi();
                return;
            }
            _measuringDpi = true;
            _listener.BeginDistance();
            IconDpi.Kind = PackIconKind.Stop;
            TxtDpiButton.Text = Str("Str_KbMouse_DpiStop");
            TxtDpiResult.Text = string.Empty;
            UpdateDpiInstruction();
        }

        private void FinishDpi()
        {
            if (!_measuringDpi || _listener == null) return;
            var (dx, dy) = _listener.ReadDistance();
            double counts = KeyboardMouseTestManager.NetDistance(dx, dy);
            _measuringDpi = false;
            ResetDpiUi();

            if (counts < 100)
            {
                TxtDpiResult.Text = Str("Str_KbMouse_DpiTooLittle");
                return;
            }
            double dpi = KeyboardMouseTestManager.MeasuredDpi(counts, SliderDpiCm.Value);
            TxtDpiResult.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_DpiResult"), dpi, KeyboardMouseTestManager.NearestCommonDpi(dpi), counts, (int)SliderDpiCm.Value);
        }

        private void ResetDpiUi()
        {
            IconDpi.Kind = PackIconKind.Play;
            TxtDpiButton.Text = Str("Str_KbMouse_DpiStart");
            TxtDpiLive.Text = string.Empty;
            UpdateDpiInstruction();
        }

        private void UpdateDpiInstruction() =>
            TxtDpiInstruction.Text = string.Format(CultureInfo.CurrentCulture, Str(_measuringDpi ? "Str_KbMouse_DpiMeasuring" : "Str_KbMouse_DpiIdle"), (int)SliderDpiCm.Value);

        // =================================================================
        //  Reaction time
        // =================================================================

        private void BtnReactionStart_Click(object sender, RoutedEventArgs e)
        {
            if (_listener == null) return;
            _reactionTimer.Stop();
            _waitStarted = _listener.Now;
            ShowReaction(ReactionState.Waiting);
            _reactionTimer.Interval = TimeSpan.FromSeconds(KeyboardMouseTestManager.ReactionDelaySeconds(_random.NextDouble()));
            _reactionTimer.Start();
        }

        private void GoSignal()
        {
            if (_listener == null || _reaction != ReactionState.Waiting) return;
            ShowReaction(ReactionState.Go);
            _goTime = _listener.Now;   // the clock starts right as the colour is set; the press time is the device's own timestamp
        }

        private void OnReactionInput(double time, string device)
        {
            if (CurrentTab != Tab.Reaction || time <= _waitStarted) return;

            if (_reaction == ReactionState.Waiting)
            {
                _reactionTimer.Stop();
                ShowReaction(ReactionState.TooEarly);
            }
            else if (_reaction == ReactionState.Go && time >= _goTime)
            {
                double ms = (time - _goTime) * 1000;
                _reactions.Add(new ReactionResult(ms, device));
                if (_reactions.Count > ReactionHistory) _reactions.RemoveAt(0);
                ShowReaction(ReactionState.Done, ms, device);
                UpdateReactionList();
            }
        }

        private void ShowReaction(ReactionState state, double ms = 0, string device = "")
        {
            _reaction = state;
            switch (state)
            {
                case ReactionState.Waiting:
                    ReactionTarget.SetResourceReference(Border.BackgroundProperty, "StatusErrorBg");
                    TxtReactionMain.Text = Str("Str_KbMouse_ReactionWait");
                    TxtReactionSub.Text = Str("Str_KbMouse_ReactionWaitHint");
                    break;
                case ReactionState.Go:
                    ReactionTarget.SetResourceReference(Border.BackgroundProperty, "StatusSuccessBg");
                    TxtReactionMain.Text = Str("Str_KbMouse_ReactionGo");
                    TxtReactionSub.Text = Str("Str_KbMouse_ReactionGoHint");
                    break;
                case ReactionState.TooEarly:
                    ReactionTarget.SetResourceReference(Border.BackgroundProperty, "StatusWarningBg");
                    TxtReactionMain.Text = Str("Str_KbMouse_ReactionEarly");
                    TxtReactionSub.Text = Str("Str_KbMouse_ReactionEarlyHint");
                    break;
                case ReactionState.Done:
                    ReactionTarget.SetResourceReference(Border.BackgroundProperty, "RowBg");
                    TxtReactionMain.Text = string.Format(CultureInfo.CurrentCulture, "{0:0} ms", ms);
                    TxtReactionSub.Text = string.Format(Str("Str_KbMouse_ReactionDone"), device);
                    break;
                default:
                    ReactionTarget.SetResourceReference(Border.BackgroundProperty, "RowBg");
                    TxtReactionMain.Text = Str("Str_KbMouse_ReactionIdle");
                    TxtReactionSub.Text = string.Empty;
                    break;
            }
            UiMotion.Pop(ReactionTarget);
        }

        private void UpdateReactionList()
        {
            ReactionList.ItemsSource = _reactions.AsEnumerable().Reverse().Select((r, i) => $"{r.Milliseconds,6:0} ms   {r.Device}").ToList();
            var s = KeyboardMouseTestManager.SummarizeReactions(_reactions);
            TxtReactionSummary.Text = s == null ? string.Empty : string.Format(CultureInfo.CurrentCulture, Str("Str_KbMouse_ReactionSummary"), s.Count, s.Average, s.Best, s.Median);
        }

        // =================================================================
        //  Reset
        // =================================================================

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            switch (CurrentTab)
            {
                case Tab.Keyboard:
                    _rollover.Reset();
                    _keyChatter.Reset();
                    _log.Clear();
                    LogList.ItemsSource = null;
                    TxtLogEmpty.Visibility = Visibility.Visible;
                    foreach (var (id, tile) in _tiles) PaintTile(id, tile);
                    UpdateKeyboardPills();
                    break;

                case Tab.Mouse:
                    _mouseChatter.Reset();
                    _wheel.Reset();
                    foreach (var row in _rows) { row.Count = 0; PaintButton(row); }
                    UpdateButtonsTable();
                    TxtMouseChatter.Text = string.Empty;
                    _listener?.ClearMoveTimes();
                    ResetPollingUi();
                    ResetMotion();
                    TxtDpiResult.Text = string.Empty;
                    break;

                default:
                    _reactions.Clear();
                    UpdateReactionList();
                    _reactionTimer.Stop();
                    ShowReaction(ReactionState.Idle);
                    break;
            }
            ShowStatus(Str("Str_KbMouse_ResetDone"), StatusKind.Info);
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
