using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using Separator = System.Windows.Controls.Separator;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The collapsible Options sidebar (same look and behaviour as the Color Picker's). Whatever is put between the tags
    /// becomes the body. Collapsing hides the body and shrinks the control to a slim icon strip.
    ///
    /// Written without a XAML file on purpose: a UserControl with its own XAML is its own name scope, and then the
    /// x:Name of every control placed inside it would clash with the host view's. This is a Border whose inside is
    /// built from a small XAML string, so the body's names stay in the host view.
    /// </summary>
    [ContentProperty(nameof(Body))]
    public sealed class ToolSidebar : Border
    {
        public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
            nameof(Body), typeof(object), typeof(ToolSidebar), new PropertyMetadata(null, (d, e) => ((ToolSidebar)d)._scroll.Content = e.NewValue));

        public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
            nameof(Header), typeof(string), typeof(ToolSidebar), new PropertyMetadata(null, (d, e) =>
            {
                if (e.NewValue is string text) ((ToolSidebar)d)._headerText.Text = text;
            }));

        public static readonly DependencyProperty HeaderIconProperty = DependencyProperty.Register(
            nameof(HeaderIcon), typeof(PackIconKind), typeof(ToolSidebar), new PropertyMetadata(PackIconKind.TuneVariant, (d, e) => ((ToolSidebar)d)._headerIcon.Kind = (PackIconKind)e.NewValue));

        public static readonly DependencyProperty ExpandedWidthProperty = DependencyProperty.Register(
            nameof(ExpandedWidth), typeof(double), typeof(ToolSidebar), new PropertyMetadata(280.0, (d, _) => ((ToolSidebar)d).ApplyWidth()));

        public const double CollapsedWidth = 56;

        private const string InnerXaml = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
      xmlns:md='http://materialdesigninxaml.net/winfx/xaml/themes'>
    <Grid.RowDefinitions>
        <RowDefinition Height='Auto' />
        <RowDefinition Height='*' />
    </Grid.RowDefinitions>
    <StackPanel>
        <Button x:Name='Toggle' MinHeight='45' HorizontalContentAlignment='Left' Cursor='Hand' FocusVisualStyle='{x:Null}'
                Foreground='{DynamicResource SidebarText}' ToolTip='{DynamicResource Str_Common_SidebarToggleTooltip}'>
            <Button.Style>
                <Style TargetType='Button'>
                    <Setter Property='Background' Value='Transparent' />
                    <Setter Property='BorderBrush' Value='Transparent' />
                    <Setter Property='OverridesDefaultStyle' Value='True' />
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='Button'>
                                <Border Background='{TemplateBinding Background}'>
                                    <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' />
                                </Border>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </Button.Style>
            <StackPanel Margin='16,0,0,0' Orientation='Horizontal'>
                <md:PackIcon x:Name='HeaderIconView' Width='20' Height='20' VerticalAlignment='Center' Kind='TuneVariant' />
                <TextBlock x:Name='HeaderText' Margin='10,0,0,0' VerticalAlignment='Center' FontWeight='SemiBold' Text='{DynamicResource Str_Common_OptionsLabel}' />
            </StackPanel>
        </Button>
        <Separator x:Name='HeaderSeparator' Margin='16,4' Style='{StaticResource SidebarSeparatorStyle}' />
    </StackPanel>
    <ScrollViewer x:Name='BodyScroll' Grid.Row='1' HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto' Padding='16,4,16,16' />
</Grid>";

        private readonly TextBlock _headerText;
        private readonly PackIcon _headerIcon;
        private readonly Separator _separator;
        private readonly ScrollViewer _scroll;
        private bool _collapsed;

        public ToolSidebar()
        {
            SetResourceReference(StyleProperty, "SidebarPanelStyle");
            var root = (FrameworkElement)XamlReader.Parse(InnerXaml);
            Child = root;

            var button = (Button)root.FindName("Toggle");
            _headerText = (TextBlock)root.FindName("HeaderText");
            _headerIcon = (PackIcon)root.FindName("HeaderIconView");
            _separator = (Separator)root.FindName("HeaderSeparator");
            _scroll = (ScrollViewer)root.FindName("BodyScroll");
            button.Click += (_, _) => IsCollapsed = !IsCollapsed;
            ApplyWidth();
        }

        /// <summary>What the sidebar holds (the XAML content).</summary>
        public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

        /// <summary>The title in the header; the standard "Options" when not set.</summary>
        public string? Header { get => (string?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

        public PackIconKind HeaderIcon { get => (PackIconKind)GetValue(HeaderIconProperty); set => SetValue(HeaderIconProperty, value); }

        public double ExpandedWidth { get => (double)GetValue(ExpandedWidthProperty); set => SetValue(ExpandedWidthProperty, value); }

        public bool IsCollapsed
        {
            get => _collapsed;
            set
            {
                if (_collapsed == value) return;
                _collapsed = value;
                var visibility = value ? Visibility.Collapsed : Visibility.Visible;
                _headerText.Visibility = visibility;
                _separator.Visibility = visibility;
                _scroll.Visibility = visibility;
                ApplyWidth();
            }
        }

        private void ApplyWidth() => Width = _collapsed ? CollapsedWidth : ExpandedWidth;
    }
}
