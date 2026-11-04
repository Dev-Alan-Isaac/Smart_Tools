using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// A small modal window that runs a copy (install, portable copy) and shows its progress with a Cancel button.
    /// Built in code, in the Windows colours, so it works before the app's theme exists and from the Settings page alike.
    /// </summary>
    public sealed class CopyProgressWindow : Window
    {
        private readonly Func<IProgress<CopyProgress>, CancellationToken, Task<InstallResult>> _work;
        private readonly CancellationTokenSource _cts = new();
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 8, Margin = new Thickness(0, 14, 0, 0) };
        private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        private bool _finished;

        public InstallResult Result { get; private set; } = new(false, true, "", null);

        private CopyProgressWindow(string title, Func<IProgress<CopyProgress>, CancellationToken, Task<InstallResult>> work)
        {
            _work = work;
            Title = AppMode.ProductName;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            FontFamily = new FontFamily("Segoe UI");
            Background = WindowsPalette.Brush(WindowsPalette.Background);

            var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = WindowsPalette.Brush(WindowsPalette.Text) };
            _status.Foreground = WindowsPalette.Brush(WindowsPalette.Muted);
            _bar.Foreground = WindowsPalette.Brush(WindowsPalette.Accent);
            _bar.Background = WindowsPalette.Brush(WindowsPalette.Line);
            _bar.BorderThickness = new Thickness(0);

            var cancel = new Button
            {
                Content = WindowsPalette.T("Cancel", "Cancelar"),
                MinWidth = 90,
                Margin = new Thickness(0, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsCancel = true,
            };
            cancel.Click += (_, _) => { cancel.IsEnabled = false; _status.Text = WindowsPalette.T("Cancelling...", "Cancelando..."); _cts.Cancel(); };

            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(heading);
            panel.Children.Add(_bar);
            panel.Children.Add(_status);
            panel.Children.Add(cancel);
            Content = panel;

            Loaded += async (_, _) =>
            {
                var progress = new Progress<CopyProgress>(p =>
                {
                    _bar.Value = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal;
                    _status.Text = p.File.Length > 0
                        ? string.Format(CultureInfo.CurrentCulture, "{0:P0} - {1}", _bar.Value, p.File)
                        : string.Format(CultureInfo.CurrentCulture, "{0:P0}", _bar.Value);
                });
                try
                {
                    Result = await _work(progress, _cts.Token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Result = new InstallResult(false, false, ex.Message, null);
                }
                _finished = true;
                Close();
            };

            Closing += (_, e) =>
            {
                if (_finished) return;
                e.Cancel = true;      // closing with the X while copying means Cancel; the window closes itself when the work stops
                _cts.Cancel();
            };
        }

        /// <summary>Shows the window, runs the work, and returns its result once it finishes or is cancelled.</summary>
        public static InstallResult Run(Window? owner, string title, Func<IProgress<CopyProgress>, CancellationToken, Task<InstallResult>> work)
        {
            var window = new CopyProgressWindow(title, work);
            if (owner != null && owner.IsVisible) window.Owner = owner;
            window.ShowDialog();
            return window.Result;
        }
    }
}
