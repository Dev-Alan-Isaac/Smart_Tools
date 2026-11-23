using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// Draws a waveform with a ruler, the part of the file that is in use (the rest is dimmed), a
    /// selection with draggable edges and a playhead. The mouse drags a new selection, moves its edges,
    /// clicks to place the playhead, and the wheel zooms (Shift + wheel scrolls). Everything is in
    /// seconds of the file. The waveform shape is cached, so moving the playhead is cheap.
    /// Colours come in as properties, so the view can bind them to theme tokens.
    /// </summary>
    public sealed class AudioWaveformControl : FrameworkElement
    {
        private const double RulerHeight = 20;
        private const double EdgeGrab = 7;          // pixels around a selection edge that grab it
        private const double ClickSlop = 3;         // pixels of movement that still count as a click
        private const double MinViewSeconds = 0.05;

        // ---- dependency properties -------------------------------------------------

        private static DependencyProperty Reg<T>(string name, T def, FrameworkPropertyMetadataOptions opts = FrameworkPropertyMetadataOptions.AffectsRender, PropertyChangedCallback? changed = null) =>
            DependencyProperty.Register(name, typeof(T), typeof(AudioWaveformControl), new FrameworkPropertyMetadata(def, opts, changed));

        private static readonly PropertyChangedCallback ShapeChanged = (d, _) => ((AudioWaveformControl)d)._shape = null;
        private static readonly PropertyChangedCallback ViewChangedCb = (d, _) =>
        {
            var c = (AudioWaveformControl)d;
            c._shape = null;
            c.ViewChanged?.Invoke(c, EventArgs.Empty);
        };

        public static readonly DependencyProperty PeaksProperty = Reg<byte[]?>(nameof(Peaks), null, changed: (d, _) =>
        {
            var c = (AudioWaveformControl)d;
            c._shape = null;
            c._peakMax = c.Peaks is { Length: > 0 } p ? Math.Max((int)p.Max(), 1) : 1;
        });
        public static readonly DependencyProperty DurationProperty = Reg(nameof(Duration), 0.0, changed: ViewChangedCb);
        public static readonly DependencyProperty ViewStartProperty = Reg(nameof(ViewStart), 0.0, changed: ViewChangedCb);
        public static readonly DependencyProperty ViewDurationProperty = Reg(nameof(ViewDuration), 0.0, changed: ViewChangedCb);
        public static readonly DependencyProperty ClipStartProperty = Reg(nameof(ClipStart), 0.0);
        public static readonly DependencyProperty ClipEndProperty = Reg(nameof(ClipEnd), 0.0);
        public static readonly DependencyProperty SelectionStartProperty = Reg(nameof(SelectionStart), double.NaN);
        public static readonly DependencyProperty SelectionEndProperty = Reg(nameof(SelectionEnd), double.NaN);
        public static readonly DependencyProperty PlayheadProperty = Reg(nameof(Playhead), double.NaN);
        public static readonly DependencyProperty AutoScaleProperty = Reg(nameof(AutoScale), true, changed: ShapeChanged);
        public static readonly DependencyProperty GainProperty = Reg(nameof(Gain), 1.0, changed: ShapeChanged);
        public static readonly DependencyProperty WaveBrushProperty = Reg<Brush?>(nameof(WaveBrush), null);
        public static readonly DependencyProperty BackBrushProperty = Reg<Brush?>(nameof(BackBrush), null);
        public static readonly DependencyProperty SelectionBrushProperty = Reg<Brush?>(nameof(SelectionBrush), null);
        public static readonly DependencyProperty SelectionEdgeBrushProperty = Reg<Brush?>(nameof(SelectionEdgeBrush), null);
        public static readonly DependencyProperty PlayheadBrushProperty = Reg<Brush?>(nameof(PlayheadBrush), null);
        public static readonly DependencyProperty TextBrushProperty = Reg<Brush?>(nameof(TextBrush), null);
        public static readonly DependencyProperty GridBrushProperty = Reg<Brush?>(nameof(GridBrush), null);

        /// <summary>Loudness slices of the whole file, 0-255, <see cref="AudioTrimmerPeaks.PerSecond"/> a second.</summary>
        public byte[]? Peaks { get => (byte[]?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
        public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
        public double ViewStart { get => (double)GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
        public double ViewDuration { get => (double)GetValue(ViewDurationProperty); set => SetValue(ViewDurationProperty, value); }
        public double ClipStart { get => (double)GetValue(ClipStartProperty); set => SetValue(ClipStartProperty, value); }
        public double ClipEnd { get => (double)GetValue(ClipEndProperty); set => SetValue(ClipEndProperty, value); }
        public double SelectionStart { get => (double)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
        public double SelectionEnd { get => (double)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
        public double Playhead { get => (double)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }

        /// <summary>Draw quiet files taller (the loudest point reaches nearly the full height).</summary>
        public bool AutoScale { get => (bool)GetValue(AutoScaleProperty); set => SetValue(AutoScaleProperty, value); }

        /// <summary>The height multiplier used when <see cref="AutoScale"/> is off (a live recording shows quiet speech better with a gain above 1).</summary>
        public double Gain { get => (double)GetValue(GainProperty); set => SetValue(GainProperty, value); }

        /// <summary>Call after the contents of <see cref="Peaks"/> changed without the array being replaced (a recording that is still growing): the drawn shape is rebuilt.</summary>
        public void RefreshPeaks()
        {
            _shape = null;
            InvalidateVisual();
        }

        public Brush? WaveBrush { get => (Brush?)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty, value); }
        public Brush? BackBrush { get => (Brush?)GetValue(BackBrushProperty); set => SetValue(BackBrushProperty, value); }
        public Brush? SelectionBrush { get => (Brush?)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
        public Brush? SelectionEdgeBrush { get => (Brush?)GetValue(SelectionEdgeBrushProperty); set => SetValue(SelectionEdgeBrushProperty, value); }
        public Brush? PlayheadBrush { get => (Brush?)GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
        public Brush? TextBrush { get => (Brush?)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
        public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

        // ---- events ----------------------------------------------------------------

        /// <summary>The user changed the selection (while dragging and when the drag ends).</summary>
        public event EventHandler? SelectionEdited;

        /// <summary>The user clicked at a time: move the playhead there.</summary>
        public event EventHandler<double>? SeekRequested;

        /// <summary>The visible part changed (zoom, scroll, a new file).</summary>
        public event EventHandler? ViewChanged;

        // ---- state -----------------------------------------------------------------

        private enum Drag { None, New, Start, End }

        private Drag _drag;
        private double _anchor;
        private Point _down;
        private bool _moved;
        private Geometry? _shape;
        private double _shapeKey;                   // width x height x view, to know when the cache is stale
        private int _peakMax = 1;

        public AudioWaveformControl()
        {
            Focusable = true;
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            Cursor = Cursors.Arrow;
        }

        public bool HasSelection => !double.IsNaN(SelectionStart) && !double.IsNaN(SelectionEnd) && Math.Abs(SelectionEnd - SelectionStart) > 1e-6;

        /// <summary>The selection with its ends in order (start, end), or null.</summary>
        public (double Start, double End)? GetSelection() =>
            HasSelection ? (Math.Min(SelectionStart, SelectionEnd), Math.Max(SelectionStart, SelectionEnd)) : null;

        public void ClearSelection()
        {
            SelectionStart = double.NaN;
            SelectionEnd = double.NaN;
        }

        // ---- view ------------------------------------------------------------------

        private double PlotHeight => Math.Max(10, ActualHeight - RulerHeight);

        private double TimeAt(double x) => ViewStart + x / Math.Max(1, ActualWidth) * ViewDuration;

        private double XAt(double t) => (t - ViewStart) / Math.Max(1e-9, ViewDuration) * ActualWidth;

        /// <summary>Shows the whole file.</summary>
        public void ZoomFit()
        {
            ViewDuration = Duration;
            ViewStart = 0;
        }

        /// <summary>Shows a part of the file, with a little room around it.</summary>
        public void ZoomToRange(double a, double b)
        {
            double len = Math.Max(MinViewSeconds, b - a);
            double pad = len * 0.08;
            SetView(a - pad, len + pad * 2);
        }

        /// <summary>Zooms by <paramref name="factor"/> (above 1 = closer) around a time.</summary>
        public void ZoomBy(double factor, double aroundTime)
        {
            if (Duration <= 0) return;
            double newLen = Math.Clamp(ViewDuration / factor, Math.Min(MinViewSeconds, Duration), Duration);
            double frac = ViewDuration > 0 ? (aroundTime - ViewStart) / ViewDuration : 0.5;
            SetView(aroundTime - frac * newLen, newLen);
        }

        /// <summary>Scrolls just enough to show <paramref name="t"/> (follows the playhead).</summary>
        public void EnsureVisible(double t)
        {
            if (Duration <= 0 || double.IsNaN(t)) return;
            if (t < ViewStart || t > ViewStart + ViewDuration * 0.95)
                SetView(t - ViewDuration * 0.1, ViewDuration);
        }

        public void SetView(double start, double length)
        {
            if (Duration <= 0) return;
            length = Math.Clamp(length, Math.Min(MinViewSeconds, Duration), Duration);
            start = Math.Clamp(start, 0, Math.Max(0, Duration - length));
            if (Math.Abs(start - ViewStart) > 1e-9) ViewStart = start;
            if (Math.Abs(length - ViewDuration) > 1e-9) ViewDuration = length;
        }

        // ---- mouse -----------------------------------------------------------------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (Duration <= 0 || Peaks == null) return;
            Focus();

            var p = e.GetPosition(this);
            if (p.Y > PlotHeight + 4) return;           // the ruler isn't clickable

            if (e.ClickCount == 2)
            {
                SelectionStart = ClipStart;
                SelectionEnd = ClipEnd;
                SelectionEdited?.Invoke(this, EventArgs.Empty);
                return;
            }

            _down = p;
            _moved = false;
            _drag = Drag.New;
            _anchor = Math.Clamp(TimeAt(p.X), 0, Duration);

            if (GetSelection() is { } sel)
            {
                if (Math.Abs(p.X - XAt(sel.Start)) <= EdgeGrab) { _drag = Drag.Start; _anchor = sel.End; _moved = true; }
                else if (Math.Abs(p.X - XAt(sel.End)) <= EdgeGrab) { _drag = Drag.End; _anchor = sel.Start; _moved = true; }
            }
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);

            if (_drag == Drag.None)
            {
                bool nearEdge = GetSelection() is { } s && (Math.Abs(p.X - XAt(s.Start)) <= EdgeGrab || Math.Abs(p.X - XAt(s.End)) <= EdgeGrab);
                Cursor = nearEdge ? Cursors.SizeWE : Cursors.IBeam;
                return;
            }

            if (!_moved && Math.Abs(p.X - _down.X) > ClickSlop) _moved = true;
            if (!_moved) return;

            double t = Math.Clamp(TimeAt(p.X), 0, Duration);
            if (_drag == Drag.New || _drag == Drag.Start || _drag == Drag.End)
            {
                SelectionStart = _anchor;
                SelectionEnd = t;
            }

            // dragging past an edge of the view scrolls it
            if (p.X < 0) SetView(ViewStart - ViewDuration * 0.02, ViewDuration);
            else if (p.X > ActualWidth) SetView(ViewStart + ViewDuration * 0.02, ViewDuration);

            SelectionEdited?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_drag == Drag.None) return;

            var mode = _drag;
            _drag = Drag.None;
            ReleaseMouseCapture();

            if (mode == Drag.New && !_moved)
            {
                // a click: place the playhead and drop the selection
                ClearSelection();
                SelectionEdited?.Invoke(this, EventArgs.Empty);
                SeekRequested?.Invoke(this, Math.Clamp(TimeAt(e.GetPosition(this).X), 0, Duration));
            }
            else if (HasSelection)
            {
                var (a, b) = GetSelection()!.Value;
                SelectionStart = a;
                SelectionEnd = b;
                SelectionEdited?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ClearSelection();
                SelectionEdited?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _drag = Drag.None;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Duration <= 0) return;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                SetView(ViewStart - Math.Sign(e.Delta) * ViewDuration * 0.15, ViewDuration);
            }
            else
            {
                double factor = Math.Pow(1.25, e.Delta / 120.0);
                ZoomBy(factor, TimeAt(e.GetPosition(this).X));
            }
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_drag == Drag.None) Cursor = Cursors.Arrow;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _shape = null;
        }

        // ---- drawing ---------------------------------------------------------------

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, plot = PlotHeight;
            if (w < 2 || h < 2) return;

            dc.DrawRectangle(BackBrush ?? Brushes.Transparent, null, new Rect(0, 0, w, h));

            if (Duration <= 0 || Peaks is not { Length: > 0 } peaks || ViewDuration <= 0) return;      // nothing to draw (the view shows its own empty state)

            DrawCenterLine(dc, w, plot);
            DrawWave(dc, peaks, w, plot);

            // the part of the file not in use is dimmed
            var dim = BackBrush ?? Brushes.Transparent;
            dc.PushOpacity(0.72);
            double xs = Math.Clamp(XAt(ClipStart), 0, w), xe = Math.Clamp(XAt(ClipEnd), 0, w);
            if (xs > 0) dc.DrawRectangle(dim, null, new Rect(0, 0, xs, plot));
            if (xe < w) dc.DrawRectangle(dim, null, new Rect(xe, 0, w - xe, plot));
            dc.Pop();

            // selection
            if (GetSelection() is { } sel)
            {
                double a = XAt(sel.Start), b = XAt(sel.End);
                double left = Math.Clamp(a, 0, w), right = Math.Clamp(b, 0, w);
                if (right > left)
                    dc.DrawRectangle(SelectionBrush ?? Brushes.Transparent, null, new Rect(left, 0, right - left, plot));
                var edge = new Pen(SelectionEdgeBrush ?? Brushes.Gray, 2);
                if (a >= 0 && a <= w) dc.DrawLine(edge, new Point(a, 0), new Point(a, plot));
                if (b >= 0 && b <= w) dc.DrawLine(edge, new Point(b, 0), new Point(b, plot));
            }

            DrawRuler(dc, w, plot);

            // playhead
            if (!double.IsNaN(Playhead))
            {
                double x = XAt(Playhead);
                if (x >= -1 && x <= w + 1)
                {
                    var brush = PlayheadBrush ?? Brushes.Red;
                    dc.DrawLine(new Pen(brush, 2), new Point(x, 0), new Point(x, plot));
                    var tri = new StreamGeometry();
                    using (var g = tri.Open())
                    {
                        g.BeginFigure(new Point(x - 5, 0), true, true);
                        g.LineTo(new Point(x + 5, 0), true, false);
                        g.LineTo(new Point(x, 7), true, false);
                    }
                    tri.Freeze();
                    dc.DrawGeometry(brush, null, tri);
                }
            }
        }

        private void DrawCenterLine(DrawingContext dc, double w, double plot) =>
            dc.DrawLine(new Pen(GridBrush ?? Brushes.Gray, 1), new Point(0, plot / 2), new Point(w, plot / 2));

        private void DrawWave(DrawingContext dc, byte[] peaks, double w, double plot)
        {
            double key = w * 1_000_003 + plot * 1009 + ViewStart * 31 + ViewDuration * 17 + (AutoScale ? 1 : 0);
            if (_shape == null || key != _shapeKey)
            {
                _shape = BuildShape(peaks, w, plot);
                _shapeKey = key;
            }
            dc.DrawGeometry(WaveBrush ?? Brushes.Gray, null, _shape);
        }

        /// <summary>One filled outline: the loudest slice under every pixel column, mirrored around the centre line.</summary>
        private Geometry BuildShape(byte[] peaks, double w, double plot)
        {
            int columns = (int)Math.Ceiling(w);
            double mid = plot / 2, half = mid - 2;
            double scale = AutoScale ? Math.Min(12, 255.0 / _peakMax * 0.92) : Math.Max(0.1, Gain);
            double perSecond = AudioTrimmerPeaks.PerSecond;
            var amps = new double[columns];

            for (int x = 0; x < columns; x++)
            {
                double t0 = ViewStart + x / w * ViewDuration, t1 = ViewStart + (x + 1) / w * ViewDuration;
                int i0 = Math.Clamp((int)Math.Floor(t0 * perSecond), 0, peaks.Length - 1);
                int i1 = Math.Clamp((int)Math.Ceiling(t1 * perSecond), i0, peaks.Length - 1);
                int max = 0;
                for (int i = i0; i <= i1; i++) if (peaks[i] > max) max = peaks[i];
                double amp = Math.Min(1.0, max / 255.0 * scale);
                // a gentle curve so quiet parts stay visible without shouting
                amps[x] = max == 0 ? 0 : Math.Max(1, Math.Pow(amp, 0.8) * half);
            }

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, mid - amps[0]), true, true);
                for (int x = 1; x < columns; x++) g.LineTo(new Point(x, mid - amps[x]), true, false);
                for (int x = columns - 1; x >= 0; x--) g.LineTo(new Point(x, mid + amps[x]), true, false);
            }
            geo.Freeze();
            return geo;
        }

        private static readonly double[] RulerSteps = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };

        private void DrawRuler(DrawingContext dc, double w, double plot)
        {
            double perPixel = ViewDuration / w;
            double step = RulerSteps.FirstOrDefault(s => s / perPixel >= 80);
            if (step <= 0) step = RulerSteps[^1];
            int decimals = step >= 1 ? 0 : step >= 0.1 ? 1 : 2;

            var pen = new Pen(GridBrush ?? Brushes.Gray, 1);
            var text = TextBrush ?? Brushes.Gray;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            double first = Math.Ceiling(ViewStart / step) * step;
            for (double t = first; t <= ViewStart + ViewDuration + step; t += step)
            {
                double x = XAt(t);
                if (x < 0 || x > w) continue;
                dc.DrawLine(pen, new Point(x, plot), new Point(x, plot + 5));
                var label = new FormattedText(AudioTrimTime.Format(t, decimals), CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, typeface, 10.5, text, dpi);
                double lx = Math.Min(Math.Max(0, x + 3), w - label.Width);
                dc.DrawText(label, new Point(lx, plot + 4));
            }
        }
    }
}
