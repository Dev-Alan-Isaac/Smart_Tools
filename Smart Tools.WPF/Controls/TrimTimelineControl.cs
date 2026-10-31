using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// A timeline with a ruler, a highlighted selection and two handles on one track: drag a handle to
    /// set the start or the end, drag the highlighted band to slide the whole selection, click the
    /// track to bring the nearest handle there, arrow keys nudge the handle that was used last
    /// (Shift = bigger steps). The handles push each other, so the selection is never shorter than
    /// <see cref="MinSelection"/>.
    /// For long clips the wheel zooms around the pointer (Shift + wheel scrolls), + and - zoom from the
    /// keyboard, and a thin overview strip under the track shows the whole clip with the visible part
    /// outlined (drag it to scroll). Everything is in seconds. Colours come in as properties, so the
    /// view can bind them to theme tokens.
    /// </summary>
    public sealed class TrimTimelineControl : FrameworkElement
    {
        private const double RulerHeight = 18;
        private const double TrackTop = 22;
        private const double OverviewHeight = 8;
        private const double OverviewGap = 8;
        private const double HandleWidth = 12;
        private const double HandleGrab = 10;       // pixels around a handle that grab it
        private const double MinTickGap = 64;       // pixels between ruler labels
        private const double ZoomStep = 0.8;        // one wheel notch shrinks the visible time to this share
        private const double MinViewSeconds = 0.5;  // the most it zooms in

        private static readonly double[] TickSteps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600 };
        private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        // ---- dependency properties -------------------------------------------------

        private static DependencyProperty Reg<T>(string name, T def, PropertyChangedCallback? changed = null) =>
            DependencyProperty.Register(name, typeof(T), typeof(TrimTimelineControl), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender, changed));

        public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), 1.0, (d, _) => ((TrimTimelineControl)d).OnMaximumChanged());
        public static readonly DependencyProperty TrackBrushProperty = Reg<Brush?>(nameof(TrackBrush), null);
        public static readonly DependencyProperty BackBrushProperty = Reg<Brush?>(nameof(BackBrush), null);
        public static readonly DependencyProperty SelectionBrushProperty = Reg<Brush?>(nameof(SelectionBrush), null);
        public static readonly DependencyProperty HandleBrushProperty = Reg<Brush?>(nameof(HandleBrush), null);
        public static readonly DependencyProperty HandleGlyphBrushProperty = Reg<Brush?>(nameof(HandleGlyphBrush), null);
        public static readonly DependencyProperty TextBrushProperty = Reg<Brush?>(nameof(TextBrush), null);
        public static readonly DependencyProperty GridBrushProperty = Reg<Brush?>(nameof(GridBrush), null);

        /// <summary>Length of the whole clip, in seconds.</summary>
        public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
        public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
        public Brush? BackBrush { get => (Brush?)GetValue(BackBrushProperty); set => SetValue(BackBrushProperty, value); }
        public Brush? SelectionBrush { get => (Brush?)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
        public Brush? HandleBrush { get => (Brush?)GetValue(HandleBrushProperty); set => SetValue(HandleBrushProperty, value); }
        public Brush? HandleGlyphBrush { get => (Brush?)GetValue(HandleGlyphBrushProperty); set => SetValue(HandleGlyphBrushProperty, value); }
        public Brush? TextBrush { get => (Brush?)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
        public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

        // ---- selection and view --------------------------------------------------------

        private enum Grab { None, Start, End, Band, View }

        private double _start;
        private double _end = 1;
        private double _viewStart;
        private double _viewLength = 1;
        private Grab _grab = Grab.None;
        private double _bandOffset;        // seconds from the band's start to where it was grabbed
        private bool _endIsActive;         // which handle the arrow keys move
        private double _hover = double.NaN;

        public double Start => _start;
        public double End => _end;

        /// <summary>The shortest selection, in seconds.</summary>
        public double MinSelection { get; set; } = 0.1;

        /// <summary>Arrow-key step, and the Shift + arrow step, in seconds.</summary>
        public double SmallStep { get; set; } = 0.1;
        public double LargeStep { get; set; } = 1;

        /// <summary>How many times the clip is magnified (1 = the whole clip is in view).</summary>
        public double ZoomFactor => Max / ViewLength;

        public bool IsZoomed => ViewLength < Max - 1e-6;

        /// <summary>Raised when the user moves something. The argument is true when the end handle was the one that moved.</summary>
        public event Action<bool>? SelectionChanged;

        /// <summary>Raised when the visible part of the clip changes (zoom or scroll).</summary>
        public event Action? ViewChanged;

        public TrimTimelineControl()
        {
            Focusable = true;
            FocusVisualStyle = null;
            Cursor = Cursors.Hand;
            SnapsToDevicePixels = true;
        }

        /// <summary>Sets the selection from code. Raises nothing.</summary>
        public void SetSelection(double start, double end)
        {
            double max = Max;
            _end = Math.Clamp(end, 0, max);
            _start = Math.Clamp(start, 0, _end);
            InvalidateVisual();
        }

        private double Max => Math.Max(Maximum, 0.001);
        private double MinSel => Math.Min(MinSelection, Max);
        private double MinView => Math.Min(Max, MinViewSeconds);
        private double ViewLength => Math.Clamp(_viewLength, MinView, Max);
        private double ViewStart => Math.Clamp(_viewStart, 0, Max - ViewLength);

        private void OnMaximumChanged()
        {
            _end = Math.Min(_end, Max);
            _start = Math.Min(_start, _end);
            _viewStart = 0;
            _viewLength = Max;   // a new clip starts fully in view
            ViewChanged?.Invoke();
        }

        private void MoveStart(double value)
        {
            double v = Math.Clamp(value, 0, Max - MinSel);
            if (v == _start && _end >= v + MinSel) return;
            _start = v;
            if (_end < _start + MinSel) _end = Math.Min(Max, _start + MinSel);
            _endIsActive = false;
            EnsureVisible(_start);
            InvalidateVisual();
            SelectionChanged?.Invoke(false);
        }

        private void MoveEnd(double value)
        {
            double v = Math.Clamp(value, MinSel, Max);
            if (v == _end && _start <= v - MinSel) return;
            _end = v;
            if (_start > _end - MinSel) _start = Math.Max(0, _end - MinSel);
            _endIsActive = true;
            EnsureVisible(_end);
            InvalidateVisual();
            SelectionChanged?.Invoke(true);
        }

        private void MoveBand(double newStart)
        {
            double length = _end - _start;
            double s = Math.Clamp(newStart, 0, Math.Max(0, Max - length));
            if (s == _start) return;
            _start = s;
            _end = s + length;
            InvalidateVisual();
            SelectionChanged?.Invoke(false);
        }

        // ---- zoom and scroll -----------------------------------------------------------

        private void SetView(double start, double length)
        {
            double len = Math.Clamp(length, MinView, Max);
            double s = Math.Clamp(start, 0, Math.Max(0, Max - len));
            if (Math.Abs(s - ViewStart) < 1e-9 && Math.Abs(len - ViewLength) < 1e-9) return;
            _viewStart = s;
            _viewLength = len;
            InvalidateVisual();
            ViewChanged?.Invoke();
        }

        /// <summary>Scales the visible time by <paramref name="factor"/> (below 1 zooms in), keeping the time at <paramref name="anchor"/> where it is on screen.</summary>
        public void ZoomBy(double factor, double anchor)
        {
            double len = Math.Clamp(ViewLength * factor, MinView, Max);
            double ratio = (anchor - ViewStart) / ViewLength;
            SetView(anchor - ratio * len, len);
        }

        public void ZoomIn() => ZoomBy(0.5, ViewStart + ViewLength / 2);

        public void ZoomOut() => ZoomBy(2, ViewStart + ViewLength / 2);

        public void ZoomReset() => SetView(0, Max);

        /// <summary>Fits the view to the selection with a little room on both sides.</summary>
        public void ZoomToSelection()
        {
            double length = Math.Max(_end - _start, MinView);
            double pad = length * 0.12;
            SetView(_start - pad, length + pad * 2);
        }

        private void EnsureVisible(double seconds)
        {
            double len = ViewLength, vs = ViewStart;
            double margin = len * 0.04;
            if (seconds < vs + margin) SetView(seconds - margin, len);
            else if (seconds > vs + len - margin) SetView(seconds + margin - len, len);
        }

        // ---- geometry ----------------------------------------------------------------

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 240 : available.Width, 78);

        private double TrackBottom => ActualHeight - OverviewHeight - OverviewGap;
        private double UsableWidth => Math.Max(1, ActualWidth - HandleWidth);
        private double XOf(double seconds) => HandleWidth / 2 + (seconds - ViewStart) / ViewLength * UsableWidth;
        private double SecondsAt(double x) => Math.Clamp(ViewStart + (x - HandleWidth / 2) / UsableWidth * ViewLength, 0, Max);
        private double OverviewX(double seconds) => seconds / Max * ActualWidth;
        private double OverviewSecondsAt(double x) => Math.Clamp(x / Math.Max(1, ActualWidth) * Max, 0, Max);
        private bool InOverview(double y) => y >= ActualHeight - OverviewHeight - OverviewGap / 2 - 2;

        // ---- mouse -------------------------------------------------------------------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            var p = e.GetPosition(this);
            double x = p.X;

            if (InOverview(p.Y))
            {
                _grab = Grab.View;
                CenterViewOn(OverviewSecondsAt(x));
                CaptureMouse();
                e.Handled = true;
                return;
            }

            double xs = XOf(_start), xe = XOf(_end);
            bool nearStart = Math.Abs(x - xs) <= HandleGrab, nearEnd = Math.Abs(x - xe) <= HandleGrab;
            if (nearStart && nearEnd) _grab = x < (xs + xe) / 2 ? Grab.Start : Grab.End;
            else if (nearStart) _grab = Grab.Start;
            else if (nearEnd) _grab = Grab.End;
            else if (x > xs && x < xe)
            {
                _grab = Grab.Band;
                _bandOffset = SecondsAt(x) - _start;
            }
            else
            {
                // On the track outside the selection: bring the nearest handle here and keep dragging it.
                _grab = Math.Abs(x - xs) <= Math.Abs(x - xe) ? Grab.Start : Grab.End;
                if (_grab == Grab.Start) MoveStart(SecondsAt(x)); else MoveEnd(SecondsAt(x));
            }

            CaptureMouse();
            e.Handled = true;
        }

        private void CenterViewOn(double seconds) => SetView(seconds - ViewLength / 2, ViewLength);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            double x = p.X;
            _hover = InOverview(p.Y) && _grab != Grab.Start && _grab != Grab.End && _grab != Grab.Band ? double.NaN : SecondsAt(x);

            switch (_grab)
            {
                case Grab.Start: MoveStart(SecondsAt(x)); break;
                case Grab.End: MoveEnd(SecondsAt(x)); break;
                case Grab.Band: MoveBand(SecondsAt(x) - _bandOffset); break;
                case Grab.View: CenterViewOn(OverviewSecondsAt(x)); break;
                default:
                    double xs = XOf(_start), xe = XOf(_end);
                    Cursor = InOverview(p.Y) ? Cursors.Hand
                        : Math.Abs(x - xs) <= HandleGrab || Math.Abs(x - xe) <= HandleGrab ? Cursors.SizeWE
                        : x > xs && x < xe ? Cursors.SizeAll
                        : Cursors.Hand;
                    break;
            }
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _grab = Grab.None;
            if (IsMouseCaptured) ReleaseMouseCapture();
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_grab != Grab.None) return;
            _hover = double.NaN;
            InvalidateVisual();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _grab = Grab.None;
        }

        /// <summary>The wheel zooms around the pointer; Shift + wheel scrolls.</summary>
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta == 0) return;

            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                SetView(ViewStart - Math.Sign(e.Delta) * ViewLength * 0.15, ViewLength);
            else
                ZoomBy(e.Delta > 0 ? ZoomStep : 1 / ZoomStep, SecondsAt(e.GetPosition(this).X));

            _hover = SecondsAt(e.GetPosition(this).X);
            e.Handled = true;
        }

        // ---- keyboard ----------------------------------------------------------------

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    ZoomIn();
                    e.Handled = true;
                    return;
                case Key.OemMinus or Key.Subtract:
                    ZoomOut();
                    e.Handled = true;
                    return;
            }

            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? LargeStep : SmallStep;
            double dir = e.Key switch { Key.Left or Key.Down => -1, Key.Right or Key.Up => 1, _ => 0 };
            if (dir == 0) return;
            if (_endIsActive) MoveEnd(_end + dir * step); else MoveStart(_start + dir * step);
            e.Handled = true;
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            InvalidateVisual();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            InvalidateVisual();
        }

        // ---- drawing -----------------------------------------------------------------

        private FormattedText Text(string s, Brush brush) =>
            new(s, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, Face, 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private static string TickLabel(double seconds, double step)
        {
            var t = TimeSpan.FromSeconds(Math.Round(seconds, 3));
            string m = ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture);
            return step < 1
                ? string.Create(CultureInfo.InvariantCulture, $"{m}:{t.Seconds:00}.{t.Milliseconds / 100}")
                : string.Create(CultureInfo.InvariantCulture, $"{m}:{t.Seconds:00}");
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < HandleWidth * 2 || h < TrackTop + 30) return;

            double trackBottom = TrackBottom;
            double trackH = trackBottom - TrackTop;
            var track = new Rect(0, TrackTop, w, trackH);
            var text = TextBrush ?? Brushes.Gray;
            var gridPen = new Pen(GridBrush ?? Brushes.Gray, 1);
            double vs = ViewStart, vl = ViewLength;

            dc.DrawRoundedRectangle(TrackBrush ?? Brushes.Transparent, null, track, 8, 8);

            // ruler: a label every step that leaves room for it, and a faint line down the track
            double step = TickSteps[^1];
            foreach (double s in TickSteps)
                if (s / vl * UsableWidth >= MinTickGap) { step = s; break; }

            for (double t = Math.Floor(vs / step) * step; t <= vs + vl + step; t += step)
            {
                if (t < -1e-9 || t > Max + 1e-9) continue;
                double x = Math.Round(XOf(t)) + 0.5;
                if (x < -1 || x > w + 1) continue;
                dc.DrawLine(gridPen, new Point(x, RulerHeight - 4), new Point(x, RulerHeight));
                dc.PushOpacity(0.45);
                dc.DrawLine(gridPen, new Point(x, TrackTop + 4), new Point(x, trackBottom - 4));
                dc.Pop();

                var label = Text(TickLabel(t, step), text);
                double lx = Math.Clamp(x - label.Width / 2, 0, Math.Max(0, w - label.Width));
                dc.DrawText(label, new Point(lx, 0));
            }

            // the part that is kept (clipped to what is in view)
            double xs = XOf(_start), xe = XOf(_end);
            double bl = Math.Clamp(xs, 0, w), br = Math.Clamp(xe, 0, w);
            if (br > bl)
            {
                dc.PushOpacity(0.32);
                dc.DrawRectangle(SelectionBrush ?? Brushes.Transparent, null, new Rect(bl, TrackTop, br - bl, trackH));
                dc.Pop();
                var edge = new Pen(HandleBrush ?? Brushes.Gray, 2);
                dc.DrawLine(edge, new Point(bl, TrackTop + 1), new Point(br, TrackTop + 1));
                dc.DrawLine(edge, new Point(bl, trackBottom - 1), new Point(br, trackBottom - 1));
            }

            if (xs > -HandleWidth && xs < w + HandleWidth) DrawHandle(dc, xs, trackH, active: !_endIsActive);
            if (xe > -HandleWidth && xe < w + HandleWidth) DrawHandle(dc, xe, trackH, active: _endIsActive);

            DrawOverview(dc, w, h);

            // the time under the pointer
            if (!double.IsNaN(_hover))
            {
                double hx = Math.Round(XOf(_hover)) + 0.5;
                dc.PushOpacity(0.6);
                dc.DrawLine(new Pen(text, 1), new Point(hx, RulerHeight), new Point(hx, trackBottom));
                dc.Pop();

                var label = Text(GifText.FormatPrecise(_hover), text);
                double lx = Math.Clamp(hx - label.Width / 2, 0, Math.Max(0, w - label.Width));
                dc.DrawRoundedRectangle(BackBrush ?? Brushes.Transparent, null, new Rect(lx - 4, 0, label.Width + 8, RulerHeight - 3), 4, 4);
                dc.DrawText(label, new Point(lx, 1));
            }
        }

        /// <summary>The whole clip as a thin strip: where the selection is, and (when zoomed) which part is on screen.</summary>
        private void DrawOverview(DrawingContext dc, double w, double h)
        {
            var strip = new Rect(0, h - OverviewHeight, w, OverviewHeight);
            dc.DrawRoundedRectangle(TrackBrush ?? Brushes.Transparent, null, strip, 4, 4);

            double sx = OverviewX(_start), ex = OverviewX(_end);
            dc.PushOpacity(0.85);
            dc.DrawRoundedRectangle(HandleBrush ?? Brushes.Gray, null, new Rect(sx, strip.Top, Math.Max(2, ex - sx), strip.Height), 3, 3);
            dc.Pop();

            if (!IsZoomed) return;
            double vx = OverviewX(ViewStart), vw = Math.Max(6, OverviewX(ViewLength));
            dc.DrawRoundedRectangle(null, new Pen(TextBrush ?? Brushes.Gray, 1.5), new Rect(vx, strip.Top - 1, Math.Min(vw, w - vx), strip.Height + 2), 4, 4);
        }

        private void DrawHandle(DrawingContext dc, double x, double trackH, bool active)
        {
            var rect = new Rect(x - HandleWidth / 2, TrackTop - 2, HandleWidth, trackH + 4);
            var outline = IsKeyboardFocused && active ? new Pen(TextBrush ?? Brushes.Gray, 1.5) : null;
            dc.DrawRoundedRectangle(HandleBrush ?? Brushes.Gray, outline, rect, 5, 5);

            var grip = new Pen(HandleGlyphBrush ?? Brushes.White, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double cy = rect.Top + rect.Height / 2;
            dc.DrawLine(grip, new Point(x - 2, cy - 7), new Point(x - 2, cy + 7));
            dc.DrawLine(grip, new Point(x + 2, cy - 7), new Point(x + 2, cy + 7));
        }
    }
}
