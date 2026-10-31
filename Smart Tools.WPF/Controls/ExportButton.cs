using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The one Export button every view uses: 32px primary style, file-export icon, "Export" label and a chevron.
    /// Clicking it opens its <see cref="FrameworkElement.ContextMenu"/> (one item per format, even if there is only
    /// one), so a view only declares the menu items and keeps its own save logic.
    /// </summary>
    public class ExportButton : Button
    {
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(ExportButton),
                new PropertyMetadata(null, (d, _) => ((ExportButton)d).ApplyLabel()));

        private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

        /// <summary>Overrides the default "Export" text (e.g. "Export report"). Leave unset for the localized default.</summary>
        public string? Label
        {
            get => (string?)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public ExportButton()
        {
            SetResourceReference(StyleProperty, "ListToolPrimaryButtonStyle");
            ApplyLabel();

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new PackIcon
            {
                Kind = PackIconKind.FileExportOutline,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(_text);
            panel.Children.Add(new PackIcon
            {
                Kind = PackIconKind.ChevronDown,
                Width = 14,
                Height = 14,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            Content = panel;
        }

        private void ApplyLabel()
        {
            if (string.IsNullOrEmpty(Label)) _text.SetResourceReference(TextBlock.TextProperty, "Str_Common_ExportHeader");
            else _text.Text = Label;
        }

        /// <summary>Raises <see cref="ButtonBase.Click"/> first (so a view can tick the last-used format), then opens the menu.</summary>
        protected override void OnClick()
        {
            base.OnClick();
            if (ContextMenu is not { } menu) return;
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
