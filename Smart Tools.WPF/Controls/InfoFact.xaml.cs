using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>A small icon + title + sentence, used in the "Good to know" cards.</summary>
    public partial class InfoFact : UserControl
    {
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(PackIconKind), typeof(InfoFact), new PropertyMetadata(PackIconKind.InformationOutline));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(InfoFact), new PropertyMetadata(""));

        public static readonly DependencyProperty BodyProperty =
            DependencyProperty.Register(nameof(Body), typeof(string), typeof(InfoFact), new PropertyMetadata(""));

        public PackIconKind Icon { get => (PackIconKind)GetValue(IconProperty); set => SetValue(IconProperty, value); }
        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string Body { get => (string)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

        public InfoFact() => InitializeComponent();
    }
}
