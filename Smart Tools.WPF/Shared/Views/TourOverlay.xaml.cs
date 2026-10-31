using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The guided-tour layer. <see cref="Start"/> takes the steps of the page that is open
    /// (see <see cref="Tour"/>); Next/Back/Skip, the arrow keys, Enter and Esc drive it.
    /// </summary>
    public partial class TourOverlay : UserControl
    {
        private const double SpotlightPadding = 6;
        private const double CardGap = 14;
        private const double EdgeMargin = 12;

        private IReadOnlyList<Tour.Step> _steps = Array.Empty<Tour.Step>();
        private int _index;

        public TourOverlay()
        {
            InitializeComponent();
        }

        public bool IsActive => Visibility == Visibility.Visible;

        /// <summary>Starts a tour over these steps (does nothing when there are none).</summary>
        public void Start(IReadOnlyList<Tour.Step> steps)
        {
            if (steps.Count == 0) return;

            _steps = steps;
            _index = 0;
            UiMotion.FadeVisibility(this, true);   // also cancels a fade-out still running from the last tour
            UiMotion.FadeIn(this, risePx: 0);
            ShowStep();
            Focus();
        }

        /// <summary>Ends the tour (also called when another page is opened).</summary>
        public void Close()
        {
            if (!IsActive) return;
            _steps = Array.Empty<Tour.Step>();
            UiMotion.FadeVisibility(this, false);
        }

        private void ShowStep()
        {
            var step = _steps[_index];
            bool last = _index == _steps.Count - 1;

            TxtTitle.Text = step.Title;
            TxtBody.Text = step.Text;
            TxtProgress.Text = string.Format(Str("Str_Tour_StepOf", "{0} of {1}"), _index + 1, _steps.Count);
            BtnBack.Visibility = _index == 0 ? Visibility.Collapsed : Visibility.Visible;
            BtnNext.Content = last ? Str("Str_Tour_Finish", "Finish") : Str("Str_Tour_Next", "Next");
            BtnSkip.Visibility = last ? Visibility.Collapsed : Visibility.Visible;

            step.Target.BringIntoView();
            UpdateLayout();
            Place(step.Target);
            UiMotion.FadeIn(Card, risePx: 8, durationMs: 200);
            BtnNext.Focus();
        }

        /// <summary>Spotlight + card for the target. Also used after a resize.</summary>
        private void Place(FrameworkElement target)
        {
            double width = ActualWidth, height = ActualHeight;
            if (width <= 0 || height <= 0 || !target.IsLoaded) return;

            Rect box;
            try
            {
                box = target.TransformToVisual(this).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                return;   // target left the visual tree (page changed under us)
            }

            // Keep the spotlight inside the page; a target scrolled partly out still shows its visible part.
            var spot = Rect.Intersect(new Rect(box.X - SpotlightPadding, box.Y - SpotlightPadding, box.Width + SpotlightPadding * 2, box.Height + SpotlightPadding * 2),
                                      new Rect(0, 0, width, height));
            if (spot.IsEmpty) spot = new Rect(width / 2 - 1, height / 2 - 1, 2, 2);

            Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, width, height)),
                new RectangleGeometry(spot, 10, 10));

            Ring.Width = spot.Width;
            Ring.Height = spot.Height;
            Canvas.SetLeft(Ring, spot.X);
            Canvas.SetTop(Ring, spot.Y);

            Card.Measure(new Size(Card.Width, double.PositiveInfinity));
            var size = Card.DesiredSize;

            // Prefer beside the target (right, then left), then under, then above; otherwise centered.
            Point at;
            if (spot.Right + CardGap + size.Width + EdgeMargin <= width)
                at = new Point(spot.Right + CardGap, spot.Top);
            else if (spot.Left - CardGap - size.Width - EdgeMargin >= 0)
                at = new Point(spot.Left - CardGap - size.Width, spot.Top);
            else if (spot.Bottom + CardGap + size.Height + EdgeMargin <= height)
                at = new Point(spot.Left, spot.Bottom + CardGap);
            else if (spot.Top - CardGap - size.Height - EdgeMargin >= 0)
                at = new Point(spot.Left, spot.Top - CardGap - size.Height);
            else
                at = new Point((width - size.Width) / 2, (height - size.Height) / 2);

            double x = Math.Clamp(at.X, EdgeMargin, Math.Max(EdgeMargin, width - size.Width - EdgeMargin));
            double y = Math.Clamp(at.Y, EdgeMargin, Math.Max(EdgeMargin, height - size.Height - EdgeMargin));

            Canvas.SetLeft(Card, x);
            Canvas.SetTop(Card, y);
            Canvas.SetLeft(CardShadow, x);
            Canvas.SetTop(CardShadow, y);
            CardShadow.Width = size.Width;
            CardShadow.Height = size.Height;
        }

        private void Next()
        {
            if (_index >= _steps.Count - 1)
            {
                Close();
                return;
            }
            _index++;
            ShowStep();
        }

        private void Back()
        {
            if (_index == 0) return;
            _index--;
            ShowStep();
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e) => Next();
        private void BtnBack_Click(object sender, RoutedEventArgs e) => Back();
        private void BtnSkip_Click(object sender, RoutedEventArgs e) => Close();
        private void Dim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;   // the dim layer swallows clicks

        private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsActive) return;

            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Right or Key.Down: Next(); break;
                case Key.Left or Key.Up: Back(); break;
                case Key.Enter or Key.Space when e.OriginalSource is not Button: Next(); break;
                default: return;   // Tab etc. stay with the buttons
            }
            e.Handled = true;
        }

        private void Overlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsActive && _index < _steps.Count) Place(_steps[_index].Target);
        }

        private static string Str(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
