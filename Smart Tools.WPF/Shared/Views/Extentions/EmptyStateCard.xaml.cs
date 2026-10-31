using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Markup;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The standard empty state for every tool: a dashed drop zone with an icon
    /// badge, a title, a hint and an optional call to action (<see cref="Action"/>,
    /// also the element's XAML content). Pure UI - no logic; the view shows and
    /// hides it (usually with <c>UiMotion.FadeVisibility</c>).
    /// </summary>
    [ContentProperty(nameof(Action))]
    public partial class EmptyStateCard : UserControl
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
            nameof(Icon), typeof(PackIconKind), typeof(EmptyStateCard), new PropertyMetadata(PackIconKind.TrayArrowDown));

        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(EmptyStateCard), new PropertyMetadata(""));

        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(EmptyStateCard), new PropertyMetadata(""));

        public static readonly DependencyProperty DescriptionMaxWidthProperty = DependencyProperty.Register(
            nameof(DescriptionMaxWidth), typeof(double), typeof(EmptyStateCard), new PropertyMetadata(360.0));

        public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
            nameof(Action), typeof(object), typeof(EmptyStateCard), new PropertyMetadata(null));

        public EmptyStateCard() => InitializeComponent();

        /// <summary>The badge icon.</summary>
        public PackIconKind Icon { get => (PackIconKind)GetValue(IconProperty); set => SetValue(IconProperty, value); }

        /// <summary>The bold headline ("Your playlist is empty").</summary>
        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value ?? ""); }

        /// <summary>The muted hint under it ("Drag audio files here or use Add").</summary>
        public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value ?? ""); }

        /// <summary>Wrap width of the hint (default 360).</summary>
        public double DescriptionMaxWidth { get => (double)GetValue(DescriptionMaxWidthProperty); set => SetValue(DescriptionMaxWidthProperty, value); }

        /// <summary>Optional call to action under the hint, e.g. a button or a row of buttons.</summary>
        public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }
    }
}
