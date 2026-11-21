using MaterialDesignThemes.Wpf;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>How the run bar's status line is coloured.</summary>
    public enum RunBarState { Idle, Working, Success, Warning, Error }

    /// <summary>
    /// The one bar every tool's main action sits in (Jakob's Law: same place, same look in every tool).
    /// Left: a status pill (icon + text) and, while <see cref="IsBusy"/>, a progress bar with its percentage.
    /// Right: <see cref="Actions"/>, the buttons the view puts in (its own Run, Stop, Save, with their own x:Names and handlers).
    /// Pure UI: the view sets <see cref="Status"/>, <see cref="State"/>, <see cref="IsBusy"/> and <see cref="Progress"/>.
    /// Rules it carries: the status always says what is going on or what just happened (Doherty / Zeigarnik / Peak-End),
    /// the progress shows at once when work starts, and the actions are right-aligned with the primary one last (Fitts).
    /// <code>
    /// &lt;controls:RunBar x:Name="Run" Status="{DynamicResource Str_X_Ready}"&gt;
    ///     &lt;Button x:Name="BtnStop" Style="{StaticResource NeutralIconButtonStyle}" .../&gt;
    ///     &lt;Button x:Name="BtnRun" Style="{StaticResource PrimaryButtonStyle}" .../&gt;
    /// &lt;/controls:RunBar&gt;
    /// </code>
    /// </summary>
    [ContentProperty(nameof(Actions))]
    public sealed class RunBar : Border
    {
        public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
            nameof(Status), typeof(string), typeof(RunBar), new PropertyMetadata("", (d, _) => ((RunBar)d).Refresh()));

        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State), typeof(RunBarState), typeof(RunBar), new PropertyMetadata(RunBarState.Idle, (d, _) => ((RunBar)d).Refresh()));

        public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
            nameof(IsBusy), typeof(bool), typeof(RunBar), new PropertyMetadata(false, (d, _) => ((RunBar)d).Refresh()));

        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(RunBar), new PropertyMetadata(-1.0, (d, _) => ((RunBar)d).Refresh()));

        public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
            nameof(Actions), typeof(object), typeof(RunBar), new PropertyMetadata(null, (d, e) => ((RunBar)d)._actions.Content = e.NewValue));

        private readonly PackIcon _icon = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBlock _text = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Border _pill = new();
        private readonly System.Windows.Controls.ProgressBar _bar = new() { MinWidth = 120, MaxWidth = 320, Height = 6, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        private readonly TextBlock _percent = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        private readonly StackPanel _actionsRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly ContentPresenter _actions = new() { VerticalAlignment = VerticalAlignment.Center };

        public RunBar()
        {
            this.SetResourceReference(StyleProperty, "CardStyle");
            Padding = new Thickness(14, 10, 14, 10);

            var dock = new DockPanel { LastChildFill = true };

            // Right: the view's own buttons. Equal 8 px gaps are set on each button by the view (SpaceSm).
            _actionsRow.Children.Add(_actions);
            DockPanel.SetDock(_actionsRow, Dock.Right);
            dock.Children.Add(_actionsRow);

            // Left: status pill, then the progress
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var pillContent = new StackPanel { Orientation = Orientation.Horizontal };
            pillContent.Children.Add(_icon);
            pillContent.Children.Add(_text);
            _pill.SetResourceReference(StyleProperty, "IconStatusPillStyle");
            _pill.Child = pillContent;
            _pill.MaxWidth = 520;
            left.Children.Add(_pill);

            _bar.SetResourceReference(StyleProperty, "MeterBarStyle");
            _percent.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            left.Children.Add(_bar);
            left.Children.Add(_percent);
            dock.Children.Add(left);

            Child = dock;
            Refresh();
        }

        /// <summary>What is going on, or what just happened ("Ready", "Compressing 3 of 12", "Saved 12 files"). Empty hides the pill.</summary>
        public string Status { get => (string)GetValue(StatusProperty); set => SetValue(StatusProperty, value ?? ""); }

        /// <summary>Colours the pill: Idle (neutral), Working (accent), Success, Warning, Error.</summary>
        public RunBarState State { get => (RunBarState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

        /// <summary>True while work runs: shows the progress bar at once.</summary>
        public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

        /// <summary>0-100, or any negative number for "unknown" (an indeterminate bar, no percentage).</summary>
        public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

        /// <summary>The view's buttons, shown at the right edge.</summary>
        public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

        private void Refresh()
        {
            string status = Status;
            _text.Text = status;
            _pill.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            var (kind, background, foreground) = State switch
            {
                RunBarState.Working => (PackIconKind.ProgressClock, "AccentColorSubtle", "AccentColor"),
                RunBarState.Success => (PackIconKind.CheckCircleOutline, "StatusSuccessBg", "StatusSuccessFg"),
                RunBarState.Warning => (PackIconKind.AlertOutline, "StatusWarningBg", "StatusWarningFg"),
                RunBarState.Error => (PackIconKind.CloseCircleOutline, "StatusErrorBg", "StatusErrorFg"),
                _ => (PackIconKind.InformationOutline, "RowBg", "TextSecondary"),
            };
            _icon.Kind = kind;
            _pill.SetResourceReference(Border.BackgroundProperty, background);
            _icon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, foreground);
            _text.SetResourceReference(TextBlock.ForegroundProperty, foreground);

            bool busy = IsBusy;
            _bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            bool known = Progress >= 0;
            _bar.IsIndeterminate = busy && !known;
            if (known) _bar.Value = Math.Min(100, Progress);
            _percent.Visibility = busy && known ? Visibility.Visible : Visibility.Collapsed;
            _percent.Text = $"{Math.Min(100, Progress):0}%";
        }
    }
}
