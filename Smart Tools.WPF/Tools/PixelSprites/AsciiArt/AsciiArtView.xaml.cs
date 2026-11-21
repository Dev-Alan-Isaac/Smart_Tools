using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// ASCII Art Maker - UI only. Turning a picture into characters, the fonts and the saving are in
    /// AsciiArtManager (+ .Export), drawing by hand in AsciiEditor, showing the grid in AsciiGridControl.
    /// Changing a Convert option remakes the picture a moment later; Draw works on the same grid, so a
    /// converted picture can be touched up by hand.
    /// </summary>
    public partial class AsciiArtView : UserControl
    {
        private const string SettingsName = "ascii_art";
        private const uint PickedMarker = 1;   // Tag of the "picked colour" item (real colours always have alpha FF)

        private AsciiSettings _s = new();
        private readonly AsciiEditor _editor = new();
        private AsciiGlyphs? _glyphs;
        private string? _sourcePath;
        private int _sourceWidth;
        private CancellationTokenSource? _cts;
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private bool _applying = true;
        private bool _loadedOnce;
        private bool _closed;
        private bool _hasGrid;
        private bool _autoFit = true;
        private bool _settingZoom;
        private AsciiMode _mode = AsciiMode.Convert;

        // drawing state
        private AsciiDrawTool _tool = AsciiDrawTool.Pencil;
        private (int X, int Y)? _anchor;
        private bool _drawing;
        private bool _drawErase;
        private int _lastX, _lastY;
        private int _textX = -1, _textY = -1, _textStartX = -1;
        private bool _textStrokeOpen;
        private uint _pickedColor;

        public AsciiArtView()
        {
            InitializeComponent();
            _debounce.Tick += Debounce_Tick;
            _editor.Changed += (a, b) =>
            {
                Stage.InvalidateRows(a, b);
                UpdateActionState();
            };
            Stage.Focusable = true;
            Stage.CellPressed += Stage_CellPressed;
            Stage.CellMoved += Stage_CellMoved;
            Stage.CellReleased += Stage_CellReleased;
            InitPictures();
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a conversion and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _closed = true;
            _debounce.Stop();
            _thumbs.Dispose();
            _cts?.Cancel();
            EndTextStroke();
            SaveSettings();
        }

        // =================================================================
        //  Start, stop, settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_closed) return;
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                _s = JsonSettings.Load<AsciiSettings>(SettingsName);
                foreach (string f in AsciiGlyphs.InstalledFonts()) CmbFont.Items.Add(f);
                BuildColorCombo();
                ApplySettingsToUi();
                RebuildGlyphs();
                _applying = false;
                UpdateLabels();
                UpdateModeUi();
                UpdateActionState();
            }
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            _debounce.Stop();
            EndTextStroke();
            SaveSettings();
        }

        private void SaveSettings()
        {
            if (!_loadedOnce) return;
            try { ReadUi(_s); JsonSettings.Save(SettingsName, _s); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Info($"ASCII art settings not saved: {ex.Message}");
            }
        }

        private void RebuildGlyphs()
        {
            try { _glyphs = AsciiGlyphs.Create(_s.FontName); }
            catch (InvalidOperationException ex)
            {
                ShowStatus(Loc.F("Str_AsciiArt_MsgNoFont", "No monospace font could be loaded: {0}", ex.Message), StatusKind.Error);
            }
        }

        private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        private static void SelectByTag(ComboBox box, string tag)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
                if ((item.Tag as string) == tag) { box.SelectedItem = item; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private void ApplySettingsToUi()
        {
            bool was = _applying;
            _applying = true;
            try
            {
                SliderColumns.Value = Math.Clamp(_s.Columns, AsciiArtManager.MinColumns, AsciiArtManager.MaxColumns);
                ChkMatchOriginal.IsChecked = _s.MatchOriginal;
                ChkMatchShapes.IsChecked = _s.MatchShapes;
                ChkSharpen.IsChecked = _s.Sharpen;
                ChkAutoLevels.IsChecked = _s.AutoLevels;
                ChkInvert.IsChecked = _s.Invert;
                SliderContrast.Value = Math.Clamp(_s.Contrast, -100, 100);
                SliderBrightness.Value = Math.Clamp(_s.Brightness, -100, 100);
                SelectByTag(CmbCharSet, _s.CharSet.ToString());
                TxtCustomChars.Text = _s.CustomChars ?? "";
                ChkUseSpace.IsChecked = _s.UseSpace;
                CmbFont.SelectedItem = CmbFont.Items.OfType<string>().FirstOrDefault(f => string.Equals(f, _s.FontName, StringComparison.OrdinalIgnoreCase))
                                       ?? CmbFont.Items.OfType<string>().FirstOrDefault();
                RadioColored.IsChecked = _s.Color == AsciiColorMode.Colored;
                RadioBlackWhite.IsChecked = _s.Color != AsciiColorMode.Colored;
                RadioPageDark.IsChecked = _s.DarkBackground;
                RadioPageLight.IsChecked = !_s.DarkBackground;
                TxtDrawChar.Text = string.IsNullOrEmpty(_s.DrawChar) ? "#" : _s.DrawChar[..1];
                ChkSymmetry.IsChecked = _s.Symmetry;
                _editor.Symmetry = _s.Symmetry;
                TxtDrawCols.Text = _s.DrawCols.ToString();
                TxtDrawRows.Text = _s.DrawRows.ToString();
                CmbDrawColor.SelectedIndex = 0;
                SelectByTag(CmbTool, AsciiDrawTool.Pencil.ToString());
                PanelCustomChars.Visibility = _s.CharSet == AsciiCharSet.Custom ? Visibility.Visible : Visibility.Collapsed;
                _settingZoom = true;
                SliderZoom.Value = 1;
                _settingZoom = false;
            }
            finally { _applying = was; }
        }

        /// <summary>Copies what the controls show into a settings object.</summary>
        private void ReadUi(AsciiSettings t)
        {
            t.Columns = (int)Math.Round(SliderColumns.Value);
            t.MatchOriginal = ChkMatchOriginal.IsChecked == true;
            t.MatchShapes = ChkMatchShapes.IsChecked == true;
            t.Sharpen = ChkSharpen.IsChecked == true;
            t.AutoLevels = ChkAutoLevels.IsChecked == true;
            t.Invert = ChkInvert.IsChecked == true;
            t.Contrast = (int)Math.Round(SliderContrast.Value);
            t.Brightness = (int)Math.Round(SliderBrightness.Value);
            t.CharSet = Enum.TryParse(TagOf(CmbCharSet), out AsciiCharSet cs) ? cs : AsciiCharSet.Classic;
            t.CustomChars = TxtCustomChars.Text;
            t.UseSpace = ChkUseSpace.IsChecked == true;
            t.FontName = CmbFont.SelectedItem as string ?? t.FontName;
            t.Color = RadioColored.IsChecked == true ? AsciiColorMode.Colored : AsciiColorMode.BlackWhite;
            t.DarkBackground = RadioPageDark.IsChecked == true;
            t.DrawChar = TxtDrawChar.Text;
            t.Symmetry = ChkSymmetry.IsChecked == true;
            if (int.TryParse(TxtDrawCols.Text, out int dc)) t.DrawCols = Math.Clamp(dc, 4, 400);
            if (int.TryParse(TxtDrawRows.Text, out int dr)) t.DrawRows = Math.Clamp(dr, 2, 400);
        }

        private AsciiSettings Snapshot()
        {
            var copy = new AsciiSettings();
            ReadUi(copy);
            return copy;
        }

        // =================================================================
        //  Picture and conversion
        // =================================================================

        private void LoadPicture(string path)
        {
            if (_closed || !File.Exists(path)) return;
            if (_editor.IsDirty && !Confirm(Loc.T("Str_AsciiArt_AskReplaceDrawing", "Replace your drawing with this picture?"))) { RestoreSelection(); return; }
            _sourcePath = path;
            _autoFit = true;
            if (_mode == AsciiMode.Draw)
            {
                _mode = AsciiMode.Convert;
                RadioModeConvert.IsChecked = true;
                UpdateModeUi();
            }
            _debounce.Stop();
            _ = RunConvertAsync();
        }

        private void ScheduleConvert()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void Debounce_Tick(object? sender, EventArgs e)
        {
            _debounce.Stop();
            _ = RunConvertAsync();
        }

        private async Task RunConvertAsync()
        {
            string? path = _sourcePath;
            if (path == null || _closed) return;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            RunProgress.Visibility = Visibility.Visible;
            HideStatus();
            try
            {
                var settings = Snapshot();
                var result = await AsciiArtManager.ConvertFileAsync(path, settings, cts.Token);
                if (cts.IsCancellationRequested || _closed) return;
                if (result.Grid == null || result.Info == null)
                {
                    ShowStatus(result.Problem switch
                    {
                        AsciiProblem.TooFewChars => Loc.T("Str_AsciiArt_MsgTooFewChars", "Pick at least two different characters (the font must be able to draw them)."),
                        _ => Loc.F("Str_AsciiArt_MsgCantOpen", "Could not read this picture: {0}", result.Detail),
                    }, StatusKind.Error);
                    return;
                }

                _s = settings;
                RebuildGlyphs();
                _sourceWidth = result.Info.SourceWidth;
                _editor.Load(result.Grid, false);
                _hasGrid = true;
                ShowGrid();
                TxtPillSize.Text = Loc.F("Str_AsciiArt_PillSize", "{0} × {1} characters · from {2} × {3} px",
                    result.Info.Cols, result.Info.Rows, result.Info.SourceWidth, result.Info.SourceHeight);
                TxtPillChars.Text = Loc.F("Str_AsciiArt_PillChars", "{0} different characters", result.Info.CharCount);
                PillSize.Visibility = PillChars.Visibility = Visibility.Visible;
                TxtHeroHint.Visibility = Visibility.Collapsed;
                TxtCharHint.Text = result.Info.IgnoredChars > 0
                    ? Loc.N(result.Info.IgnoredChars, "Str_AsciiArt_IgnoredOne", "{0} character this font cannot draw was left out.", "Str_AsciiArt_IgnoredMany", "{0} characters this font cannot draw were left out.", result.Info.IgnoredChars)
                    : "";
            }
            catch (OperationCanceledException)
            {
                // a newer conversion took over
            }
            finally
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                    RunProgress.Visibility = Visibility.Collapsed;
                }
                cts.Dispose();
            }
        }

        /// <summary>Show the editor's grid on the stage (after a conversion, a new page colour, a resize).</summary>
        private void ShowGrid()
        {
            Stage.SetContent(_editor.Grid, _glyphs, _s.DarkBackground);
            Stage.IsInteractive = _mode == AsciiMode.Draw;
            EmptyState.Visibility = _hasGrid ? Visibility.Collapsed : Visibility.Visible;
            UpdateActionState();
            if (_autoFit) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitToStage));
        }

        // =================================================================
        //  Options
        // =================================================================

        private void Option_Click(object sender, RoutedEventArgs e) => OnOptionChanged();
        private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnOptionChanged();
        private void SliderColumns_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => OnOptionChanged();
        private void SliderTone_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => OnOptionChanged();

        private void TxtCustomChars_TextChanged(object sender, TextChangedEventArgs e) => OnOptionChanged();

        private void OnOptionChanged()
        {
            if (_applying || !_loadedOnce || _closed) return;
            UpdateLabels();
            PanelCustomChars.Visibility = TagOf(CmbCharSet) == nameof(AsciiCharSet.Custom) ? Visibility.Visible : Visibility.Collapsed;

            var next = Snapshot();
            bool remake = _mode == AsciiMode.Convert && _sourcePath != null;
            bool pageOnly = _mode == AsciiMode.Draw || _sourcePath == null;
            if (remake && _editor.IsDirty && !Confirm(Loc.T("Str_AsciiArt_AskRemake", "Changing this makes the picture again and your hand-drawn changes are lost. Continue?")))
            {
                ApplySettingsToUi();   // put the controls back as they were
                UpdateLabels();
                return;
            }
            bool fontChanged = !string.Equals(next.FontName, _s.FontName, StringComparison.OrdinalIgnoreCase);
            _s = next;
            if (fontChanged) RebuildGlyphs();
            if (remake) ScheduleConvert();
            else if (pageOnly && (_hasGrid || fontChanged)) ShowGrid();
            else Stage.SetContent(_editor.Grid, _glyphs, _s.DarkBackground);
        }

        private void UpdateLabels()
        {
            bool auto = ChkMatchOriginal.IsChecked == true;
            SliderColumns.IsEnabled = !auto;
            TxtColumnsLabel.Text = auto
                ? Loc.T("Str_AsciiArt_ColumnsAuto", "Characters across: follows the picture")
                : Loc.F("Str_AsciiArt_ColumnsLabel", "Characters across: {0}", (int)Math.Round(SliderColumns.Value));
            TxtContrastLabel.Text = Loc.F("Str_AsciiArt_ContrastLabel", "Contrast: {0}", (int)Math.Round(SliderContrast.Value));
            TxtBrightnessLabel.Text = Loc.F("Str_AsciiArt_BrightnessLabel", "Brightness: {0}", (int)Math.Round(SliderBrightness.Value));
        }

        // =================================================================
        //  Mode: Convert / Draw
        // =================================================================

        private void RadioMode_Click(object sender, RoutedEventArgs e)
        {
            var wanted = RadioModeDraw.IsChecked == true ? AsciiMode.Draw : AsciiMode.Convert;
            if (wanted == _mode) return;
            EndTextStroke();
            _mode = wanted;
            if (_mode == AsciiMode.Draw && !_hasGrid)
            {
                var cur = Snapshot();
                _s.DrawCols = cur.DrawCols;
                _s.DrawRows = cur.DrawRows;
                _editor.Load(new AsciiGrid(_s.DrawCols, _s.DrawRows), false);
                _hasGrid = true;
                _autoFit = true;
            }
            UpdateModeUi();
            ShowGrid();
        }

        private void UpdateModeUi()
        {
            bool draw = _mode == AsciiMode.Draw;
            PanelConvert.Visibility = draw ? Visibility.Collapsed : Visibility.Visible;
            PanelDraw.Visibility = draw ? Visibility.Visible : Visibility.Collapsed;
            GridColorMode.Visibility = draw ? Visibility.Collapsed : Visibility.Visible;
            TxtModeHint.Text = draw
                ? Loc.T("Str_AsciiArt_HintDraw", "Paint characters by hand. If you converted a picture first, you are touching up that result.")
                : Loc.T("Str_AsciiArt_HintConvert", "Open a picture and it becomes characters. Change an option and it is remade.");
            Stage.IsInteractive = draw;
            if (draw) UpdateToolHint();
            else { Stage.Ghost = null; Stage.InvalidateVisual(); }
        }

        // =================================================================
        //  Draw
        // =================================================================

        private void BuildColorCombo()
        {
            (string Key, string Fallback, uint Argb)[] colors =
            {
                ("Default", "Default", 0),
                ("White", "White", 0xFFFFFFFF),
                ("Black", "Black", 0xFF000000),
                ("Red", "Red", 0xFFE5484D),
                ("Orange", "Orange", 0xFFF76B15),
                ("Yellow", "Yellow", 0xFFF5D90A),
                ("Green", "Green", 0xFF30A46C),
                ("Teal", "Teal", 0xFF12A594),
                ("Blue", "Blue", 0xFF3E63DD),
                ("Purple", "Purple", 0xFF8E4EC6),
                ("Pink", "Pink", 0xFFE93D82),
                ("Gray", "Gray", 0xFF8B8D98),
            };
            foreach (var (key, fallback, argb) in colors) CmbDrawColor.Items.Add(ColorItem(Loc.T("Str_AsciiArt_Col_" + key, fallback), argb, argb));
            CmbDrawColor.Items.Add(ColorItem(Loc.T("Str_AsciiArt_Col_Picked", "Picked from the picture"), 0xFF8B8D98, PickedMarker));
        }

        private ComboBoxItem ColorItem(string name, uint swatch, uint tag)
        {
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var dot = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (swatch == 0)
            {
                dot.Fill = Brushes.Transparent;
                dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SidebarTextMuted");
                dot.StrokeThickness = 1;
            }
            else
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb((byte)(swatch >> 16), (byte)(swatch >> 8), (byte)swatch));
            }
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            return new ComboBoxItem { Content = row, Tag = tag };
        }

        private char DrawChar => string.IsNullOrEmpty(TxtDrawChar.Text) ? '#' : TxtDrawChar.Text[0];

        private uint DrawColor
        {
            get
            {
                if ((CmbDrawColor.SelectedItem as ComboBoxItem)?.Tag is not uint tag) return 0;
                return tag == PickedMarker ? _pickedColor : tag;
            }
        }

        private void CmbTool_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loadedOnce) return;
            EndTextStroke();
            _textX = _textY = -1;
            Stage.Ghost = null;
            _tool = Enum.TryParse(TagOf(CmbTool), out AsciiDrawTool t) ? t : AsciiDrawTool.Pencil;
            UpdateToolHint();
            Stage.InvalidateVisual();
        }

        private void UpdateToolHint() => TxtToolHint.Text = Loc.T("Str_AsciiArt_ToolHint_" + _tool, _tool switch
        {
            AsciiDrawTool.Pencil => "Click or drag to paint with your character. Right button erases.",
            AsciiDrawTool.Eraser => "Click or drag to clear cells.",
            AsciiDrawTool.Fill => "Fills the connected cells that look like the one you click.",
            AsciiDrawTool.Line => "Drag from one cell to another.",
            AsciiDrawTool.Rectangle => "Drag a corner to the opposite corner.",
            AsciiDrawTool.Text => "Click a cell, then type. Enter starts a new line, Esc stops.",
            _ => "Click a cell to use its character and colour.",
        });

        private void TxtDrawChar_TextChanged(object sender, TextChangedEventArgs e) { }

        private void CmbDrawColor_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void ChkSymmetry_Click(object sender, RoutedEventArgs e) => _editor.Symmetry = ChkSymmetry.IsChecked == true;

        private void Stage_CellPressed(object? sender, AsciiCellEventArgs e)
        {
            if (_mode != AsciiMode.Draw) return;
            _drawErase = e.IsRightButton || _tool == AsciiDrawTool.Eraser;
            char ch = _drawErase ? ' ' : DrawChar;
            uint col = _drawErase ? 0 : DrawColor;
            switch (_tool)
            {
                case AsciiDrawTool.Pencil:
                case AsciiDrawTool.Eraser:
                    EndTextStroke();
                    _editor.BeginStroke();
                    _editor.Set(e.X, e.Y, ch, col);
                    _editor.Commit();
                    _drawing = true;
                    _lastX = e.X; _lastY = e.Y;
                    break;
                case AsciiDrawTool.Fill:
                    EndTextStroke();
                    _editor.BeginStroke();
                    _editor.Fill(e.X, e.Y, ch, col);
                    _editor.EndStroke();
                    _editor.Commit();
                    break;
                case AsciiDrawTool.Line:
                case AsciiDrawTool.Rectangle:
                    EndTextStroke();
                    _anchor = (e.X, e.Y);
                    _drawing = true;
                    Stage.Ghost = ShapeCells(e.X, e.Y, e.X, e.Y);
                    Stage.InvalidateVisual();
                    break;
                case AsciiDrawTool.Text:
                    EndTextStroke();
                    _textX = _textStartX = e.X;
                    _textY = e.Y;
                    Stage.Ghost = new() { (_textX, _textY) };
                    Stage.InvalidateVisual();
                    Stage.Focus();
                    break;
                case AsciiDrawTool.Picker:
                    PickAt(e.X, e.Y);
                    break;
            }
            UpdateActionState();
        }

        private void Stage_CellMoved(object? sender, AsciiCellEventArgs e)
        {
            if (_mode != AsciiMode.Draw || !_drawing) return;
            switch (_tool)
            {
                case AsciiDrawTool.Pencil:
                case AsciiDrawTool.Eraser:
                    _editor.Line(_lastX, _lastY, e.X, e.Y, _drawErase ? ' ' : DrawChar, _drawErase ? 0 : DrawColor);
                    _editor.Commit();
                    _lastX = e.X; _lastY = e.Y;
                    break;
                case AsciiDrawTool.Line:
                case AsciiDrawTool.Rectangle:
                    if (_anchor is { } a)
                    {
                        Stage.Ghost = ShapeCells(a.X, a.Y, e.X, e.Y);
                        Stage.InvalidateVisual();
                    }
                    break;
            }
        }

        private void Stage_CellReleased(object? sender, AsciiCellEventArgs e)
        {
            if (_mode != AsciiMode.Draw) return;
            bool erase = _drawErase;
            _drawing = false;
            switch (_tool)
            {
                case AsciiDrawTool.Pencil:
                case AsciiDrawTool.Eraser:
                    _editor.EndStroke();
                    break;
                case AsciiDrawTool.Line:
                case AsciiDrawTool.Rectangle:
                    if (_anchor is { } a)
                    {
                        _editor.BeginStroke();
                        char ch = erase ? ' ' : DrawChar;
                        uint col = erase ? 0 : DrawColor;
                        if (_tool == AsciiDrawTool.Line) _editor.Line(a.X, a.Y, e.X, e.Y, ch, col);
                        else _editor.Rectangle(a.X, a.Y, e.X, e.Y, ch, col);
                        _editor.EndStroke();
                        _editor.Commit();
                    }
                    _anchor = null;
                    Stage.Ghost = null;
                    Stage.InvalidateVisual();
                    break;
            }
            UpdateActionState();
        }

        /// <summary>The cells a line or rectangle would cover (for the preview while dragging).</summary>
        private List<(int X, int Y)> ShapeCells(int x0, int y0, int x1, int y1)
        {
            var cells = new List<(int, int)>();
            void Line(int ax, int ay, int bx, int by)
            {
                int dx = Math.Abs(bx - ax), dy = -Math.Abs(by - ay), sx = ax < bx ? 1 : -1, sy = ay < by ? 1 : -1, err = dx + dy;
                while (true)
                {
                    cells.Add((ax, ay));
                    if (ax == bx && ay == by) break;
                    int e2 = 2 * err;
                    if (e2 >= dy) { err += dy; ax += sx; }
                    if (e2 <= dx) { err += dx; ay += sy; }
                }
            }
            if (_tool == AsciiDrawTool.Line) Line(x0, y0, x1, y1);
            else { Line(x0, y0, x1, y0); Line(x1, y0, x1, y1); Line(x1, y1, x0, y1); Line(x0, y1, x0, y0); }
            return cells;
        }

        private void PickAt(int x, int y)
        {
            var g = _editor.Grid;
            if (!g.Contains(x, y)) return;
            char c = g[x, y];
            if (c != ' ') TxtDrawChar.Text = c.ToString();
            uint color = g.ColorAt(x, y);
            if (color != 0)
            {
                _pickedColor = color;
                CmbDrawColor.SelectedIndex = CmbDrawColor.Items.Count - 1;
            }
            else CmbDrawColor.SelectedIndex = 0;
        }

        // ---- typing (Text tool) and keys ----

        private bool TextToolActive => _mode == AsciiMode.Draw && _tool == AsciiDrawTool.Text && _textX >= 0 && Keyboard.FocusedElement is not TextBox;

        private void EndTextStroke()
        {
            if (!_textStrokeOpen) return;
            _textStrokeOpen = false;
            _editor.EndStroke();
        }

        private void Root_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!TextToolActive || string.IsNullOrEmpty(e.Text)) return;
            if (!_textStrokeOpen) { _editor.BeginStroke(); _textStrokeOpen = true; }
            foreach (char c in e.Text)
            {
                if (char.IsControl(c)) continue;
                if (_textX >= _editor.Grid.Cols) break;
                _editor.Text(_textX, _textY, c.ToString(), DrawColor);
                _textX++;
            }
            _editor.Commit();
            ShowCaret();
            e.Handled = true;
        }

        private void ShowCaret()
        {
            Stage.Ghost = _textX >= 0 && _editor.Grid.Contains(Math.Min(_textX, _editor.Grid.Cols - 1), _textY)
                ? new() { (Math.Min(_textX, _editor.Grid.Cols - 1), _textY) }
                : null;
            Stage.InvalidateVisual();
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_mode != AsciiMode.Draw || Keyboard.FocusedElement is TextBox) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { Undo(); e.Handled = true; return; }
            if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Shift) != 0))) { Redo(); e.Handled = true; return; }

            if (!TextToolActive) return;
            switch (e.Key)
            {
                case Key.Back:
                    if (_textX > 0)
                    {
                        if (!_textStrokeOpen) { _editor.BeginStroke(); _textStrokeOpen = true; }
                        _textX--;
                        _editor.Text(_textX, _textY, " ", 0);
                        _editor.Commit();
                        ShowCaret();
                    }
                    e.Handled = true;
                    break;
                case Key.Enter:
                    EndTextStroke();
                    _textX = _textStartX;
                    _textY = Math.Min(_textY + 1, _editor.Grid.Rows - 1);
                    ShowCaret();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    EndTextStroke();
                    _textX = _textY = -1;
                    ShowCaret();
                    e.Handled = true;
                    break;
            }
        }

        private void Undo()
        {
            EndTextStroke();
            _editor.Undo();
            UpdateActionState();
        }

        private void Redo()
        {
            EndTextStroke();
            _editor.Redo();
            UpdateActionState();
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e) => Undo();
        private void BtnRedo_Click(object sender, RoutedEventArgs e) => Redo();

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            EndTextStroke();
            _editor.Clear();
            UpdateActionState();
        }

        private void BtnApplySize_Click(object sender, RoutedEventArgs e)
        {
            var (cols, rows) = ReadDrawSize();
            EndTextStroke();
            _editor.Resize(cols, rows);
            _hasGrid = true;
            _autoFit = true;
            ShowGrid();
        }

        private void BtnNewBlank_Click(object sender, RoutedEventArgs e)
        {
            if (_editor.IsDirty && !Confirm(Loc.T("Str_AsciiArt_AskNewBlank", "Start a blank drawing? Your current drawing is lost."))) return;
            var (cols, rows) = ReadDrawSize();
            EndTextStroke();
            _editor.Load(new AsciiGrid(cols, rows), false);
            _hasGrid = true;
            _autoFit = true;
            ShowGrid();
        }

        private (int Cols, int Rows) ReadDrawSize()
        {
            int cols = int.TryParse(TxtDrawCols.Text, out int c) ? Math.Clamp(c, 4, 400) : 80;
            int rows = int.TryParse(TxtDrawRows.Text, out int r) ? Math.Clamp(r, 2, 400) : 40;
            while ((long)cols * rows > AsciiArtManager.MaxCells) { cols = Math.Max(4, cols * 9 / 10); rows = Math.Max(2, rows * 9 / 10); }
            TxtDrawCols.Text = cols.ToString();
            TxtDrawRows.Text = rows.ToString();
            return (cols, rows);
        }

        // =================================================================
        //  Zoom
        // =================================================================

        private void SliderZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ZoomScale == null || TxtZoom == null) return;   // raised while the page is still being built
            ZoomScale.ScaleX = ZoomScale.ScaleY = SliderZoom.Value;
            TxtZoom.Text = $"{SliderZoom.Value * 100:0}%";
            if (!_settingZoom) _autoFit = false;
        }

        private void BtnFit_Click(object sender, RoutedEventArgs e)
        {
            _autoFit = true;
            FitToStage();
        }

        private void FitToStage()
        {
            if (!_hasGrid || Stage.CellWidth <= 0) return;
            double w = StageScroll.ViewportWidth - 28, h = StageScroll.ViewportHeight - 28;
            if (w <= 0 || h <= 0) return;
            double gw = _editor.Grid.Cols * Stage.CellWidth, gh = _editor.Grid.Rows * Stage.CellHeight;
            double fit = Math.Clamp(Math.Min(w / gw, h / gh), SliderZoom.Minimum, SliderZoom.Maximum);
            _settingZoom = true;
            SliderZoom.Value = fit;
            _settingZoom = false;
        }

        // =================================================================
        //  Save and copy
        // =================================================================

        private void UpdateActionState()
        {
            bool has = _hasGrid && !_editor.Grid.IsBlank;
            BtnSave.IsEnabled = BtnCopyText.IsEnabled = BtnCopyRich.IsEnabled = has;
            BtnUndo.IsEnabled = _editor.CanUndo;
            BtnRedo.IsEnabled = _editor.CanRedo;
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_glyphs == null || !_hasGrid) return;
            var s = Snapshot();
            string filter = string.Join("|",
                Loc.T("Str_AsciiArt_FilterTxt", "Plain text (*.txt)") + "|*.txt",
                Loc.T("Str_AsciiArt_FilterRtf", "Rich text with colours (*.rtf)") + "|*.rtf",
                Loc.T("Str_AsciiArt_FilterHtml", "Web page (*.html)") + "|*.html",
                Loc.T("Str_AsciiArt_FilterPng", "Picture (*.png)") + "|*.png",
                Loc.T("Str_AsciiArt_FilterAnsi", "Coloured terminal text (*.ansi)") + "|*.ansi");
            var dlg = new SaveFileDialog
            {
                Title = Loc.T("Str_AsciiArt_SaveAs", "Save as..."),
                Filter = filter,
                FilterIndex = s.Color == AsciiColorMode.Colored ? 2 : 1,
                FileName = _sourcePath != null ? Path.GetFileNameWithoutExtension(_sourcePath) : Loc.T("Str_AsciiArt_DefaultName", "ascii-art"),
                OverwritePrompt = false,   // a file is never replaced: a number is added instead
            };
            if (dlg.ShowDialog() != true) return;
            BtnSave.IsEnabled = false;
            RunProgress.Visibility = Visibility.Visible;
            try
            {
                var result = await AsciiArtManager.SaveAsync(dlg.FileName, _editor.Grid.Clone(), s, _glyphs, _sourceWidth);
                ShowStatus(Loc.F("Str_AsciiArt_MsgSaved", "Saved {0}", Path.GetFileName(result.Path)) +
                           (result.PngReduced ? " " + Loc.T("Str_AsciiArt_MsgPngReduced", "(the picture was made smaller than the original to keep it a safe size)") : ""),
                    StatusKind.Success);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ExternalException)
            {
                Log.Warn($"ASCII art save failed: {ex.Message}");
                ShowStatus(Loc.F("Str_AsciiArt_MsgSaveFailed", "Could not save: {0}", ex.Message), StatusKind.Error);
            }
            finally
            {
                RunProgress.Visibility = Visibility.Collapsed;
                UpdateActionState();
            }
        }

        private void BtnCopyText_Click(object sender, RoutedEventArgs e) => Copy(rich: false);
        private void BtnCopyRich_Click(object sender, RoutedEventArgs e) => Copy(rich: true);

        private void Copy(bool rich)
        {
            if (_glyphs == null || !_hasGrid) return;
            try
            {
                Clipboard.SetDataObject(AsciiArtManager.BuildClipboard(_editor.Grid, Snapshot(), _glyphs, rich), true);
                ShowStatus(rich
                    ? Loc.T("Str_AsciiArt_MsgCopiedRich", "Copied with colours. Paste into Word, WordPad or an email.")
                    : Loc.T("Str_AsciiArt_MsgCopied", "Copied as plain text."), StatusKind.Success);
            }
            catch (ExternalException)
            {
                ShowStatus(Loc.T("Str_AsciiArt_MsgClipboardBusy", "Windows would not let this program use the clipboard right now. Try again."), StatusKind.Error);
            }
        }

        // =================================================================
        //  Messages
        // =================================================================

        private enum StatusKind { Info, Success, Error }

        private void ShowStatus(string text, StatusKind kind)
        {
            (string bg, string fg) = kind switch
            {
                StatusKind.Success => ("StatusSuccessBg", "StatusSuccessFg"),
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg"),
                _ => ("StatusInfoBg", "StatusInfoFg"),
            };
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        private static bool Confirm(string text) =>
            MessageBox.Show(text, Loc.T("Str_Tool_ascii-art_Name", "ASCII Art Maker"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}
