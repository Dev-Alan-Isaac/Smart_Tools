using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// A calendar + time pop-up that writes its pick into <see cref="Target"/> (invariant, sortable text).
    /// With <see cref="IncludeTime"/> off it is a date-only picker and a click on a day commits straight away.
    /// </summary>
    public partial class DateTimePickerButton : UserControl
    {
        public static readonly DependencyProperty TargetProperty =
            DependencyProperty.Register(nameof(Target), typeof(TextBox), typeof(DateTimePickerButton), new PropertyMetadata(null));

        public static readonly DependencyProperty IncludeTimeProperty =
            DependencyProperty.Register(nameof(IncludeTime), typeof(bool), typeof(DateTimePickerButton),
                new PropertyMetadata(true, (d, _) => ((DateTimePickerButton)d).ApplyMode()));

        public static readonly DependencyProperty IsoSeparatorProperty =
            DependencyProperty.Register(nameof(IsoSeparator), typeof(bool), typeof(DateTimePickerButton), new PropertyMetadata(false));

        /// <summary>The text box that receives the picked value and seeds the pop-up.</summary>
        public TextBox? Target { get => (TextBox?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

        /// <summary>Show the time row and write the time too.</summary>
        public bool IncludeTime { get => (bool)GetValue(IncludeTimeProperty); set => SetValue(IncludeTimeProperty, value); }

        /// <summary>Write "yyyy-MM-ddTHH:mm:ss" instead of "yyyy-MM-dd HH:mm:ss".</summary>
        public bool IsoSeparator { get => (bool)GetValue(IsoSeparatorProperty); set => SetValue(IsoSeparatorProperty, value); }

        private bool _seeding;

        public DateTimePickerButton()
        {
            InitializeComponent();
            ApplyMode();
        }

        private void ApplyMode() => TimeRow.Visibility = IncludeTime ? Visibility.Visible : Visibility.Collapsed;

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            if (PickPopup.IsOpen) { PickPopup.IsOpen = false; return; }
            Seed(ReadTarget() ?? DateTime.Now);
            PickPopup.IsOpen = true;
        }

        private void PickPopup_Closed(object? sender, EventArgs e) => Mouse.Capture(null);

        /// <summary>The Target's current text as a date, or null when it isn't one (a word, a Unix number, empty).</summary>
        private DateTime? ReadTarget()
        {
            string t = Target?.Text.Trim() ?? "";
            if (t.Length == 0) return null;
            if (DateTime.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var dt)
                || DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt))
                return dt;
            return null;
        }

        private void Seed(DateTime value)
        {
            _seeding = true;
            PickCalendar.SelectedDate = value.Date;
            PickCalendar.DisplayDate = value.Date;
            TxtHour.Text = value.Hour.ToString("00");
            TxtMinute.Text = value.Minute.ToString("00");
            TxtSecond.Text = value.Second.ToString("00");
            _seeding = false;
        }

        // Calendar keeps the mouse captured after a click, which would swallow the first click on anything else.
        private void PickCalendar_GotMouseCapture(object sender, MouseEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.Primitives.CalendarDayButton
                or System.Windows.Controls.Primitives.CalendarItem) Mouse.Capture(null);
        }

        private void PickCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_seeding || IncludeTime || PickCalendar.SelectedDate is null) return;
            Commit();
        }

        private void TimePart_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
            e.Handled = !e.Text.All(char.IsDigit);

        private void TimePart_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box) return;
            int max = box == TxtHour ? 23 : 59;
            int.TryParse(box.Text, out int n);
            box.Text = Math.Clamp(n, 0, max).ToString("00");
        }

        private void TimePart_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();

        private void Step(TextBox box, int delta)
        {
            int max = box == TxtHour ? 23 : 59;
            int.TryParse(box.Text, out int n);
            n += delta;
            box.Text = (n > max ? 0 : n < 0 ? max : n).ToString("00");
            box.SelectAll();
        }

        private void TimePart_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            Step((TextBox)sender, e.Key == Key.Up ? 1 : -1);
            e.Handled = true;
        }

        private void TimePart_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            Step((TextBox)sender, e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }

        private void BtnPickNow_Click(object sender, RoutedEventArgs e)
        {
            Seed(DateTime.Now);
            if (!IncludeTime) Commit();
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e) => Commit();

        private void Commit()
        {
            if (Target is null) { PickPopup.IsOpen = false; return; }
            DateTime day = PickCalendar.SelectedDate ?? DateTime.Today;
            string format = !IncludeTime ? "yyyy-MM-dd" : IsoSeparator ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd HH:mm:ss";
            int.TryParse(TxtHour.Text, out int h);
            int.TryParse(TxtMinute.Text, out int m);
            int.TryParse(TxtSecond.Text, out int s);
            var value = day.Date.AddHours(Math.Clamp(h, 0, 23)).AddMinutes(Math.Clamp(m, 0, 59)).AddSeconds(Math.Clamp(s, 0, 59));
            PickPopup.IsOpen = false;
            Target.Text = value.ToString(format, CultureInfo.InvariantCulture);
            Target.Focus();
            Target.CaretIndex = Target.Text.Length;
        }
    }
}
