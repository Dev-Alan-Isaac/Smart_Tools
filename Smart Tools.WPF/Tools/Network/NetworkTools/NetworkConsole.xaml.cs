using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// The results console shared by the network tools. UI only: lines are
    /// <see cref="ConsoleLine"/> records; the view decides what to log.
    /// Keeps the last <see cref="NetworkToolsManager.MaxConsoleLines"/> lines and
    /// only follows new lines when already scrolled to the bottom.
    /// </summary>
    public partial class NetworkConsole : UserControl
    {
        private readonly ObservableCollection<ConsoleLine> _lines = new();

        public NetworkConsole()
        {
            InitializeComponent();
            ConsoleList.ItemsSource = _lines;
        }

        /// <summary>The card's title (default: the shared "Console" string).</summary>
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(NetworkConsole),
            new PropertyMetadata(null, (d, e) =>
            {
                if (d is NetworkConsole c && c.HeaderLabel != null)
                    c.HeaderLabel.Content = e.NewValue as string ?? "";
            }));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>
        /// The console can be folded to just its header. A page that wants it out of the way until something happens
        /// sets <see cref="CollapseUntilFirstLog"/>; a new line always opens it again, and the user can fold it back.
        /// </summary>
        public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
            nameof(IsCollapsed), typeof(bool), typeof(NetworkConsole),
            new PropertyMetadata(false, (d, e) => ((NetworkConsole)d).ApplyCollapsed((bool)e.NewValue)));

        public bool IsCollapsed
        {
            get => (bool)GetValue(IsCollapsedProperty);
            set => SetValue(IsCollapsedProperty, value);
        }

        /// <summary>Starts folded when nothing was logged yet (set in XAML); the first line opens it.</summary>
        public bool CollapseUntilFirstLog
        {
            get => _collapseUntilFirstLog;
            set
            {
                _collapseUntilFirstLog = value;
                if (value && _lines.Count == 0) IsCollapsed = true;
            }
        }

        /// <summary>Raised when the console is folded or opened (the page changes the row height that holds it).</summary>
        public event EventHandler<bool>? CollapsedChanged;

        private bool _collapseUntilFirstLog;

        private void ApplyCollapsed(bool collapsed)
        {
            if (BodyBorder == null) return;
            BodyBorder.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            HeaderGrid.Margin = new Thickness(0, 0, 0, collapsed ? 0 : 10);
            IconToggle.Kind = collapsed ? MaterialDesignThemes.Wpf.PackIconKind.ChevronUp : MaterialDesignThemes.Wpf.PackIconKind.ChevronDown;
            BtnToggle.SetResourceReference(ToolTipProperty, collapsed ? "Str_NetworkConsole_Expand" : "Str_NetworkConsole_Collapse");
            CollapsedChanged?.Invoke(this, collapsed);
        }

        private void BtnToggle_Click(object sender, RoutedEventArgs e) => IsCollapsed = !IsCollapsed;

        /// <summary>What the empty console says ("Each ping appears here").</summary>
        public string EmptyText
        {
            get => TxtEmpty.Text;
            set => TxtEmpty.Text = value;
        }

        /// <summary>Start of the saved file's name ("Ping log" → "Ping log 2026-10-09 14-30.txt").</summary>
        public string FileNamePrefix { get; set; } = "Network log";

        public int Count => _lines.Count;

        public IReadOnlyList<ConsoleLine> Lines => _lines;

        public void Log(ConsoleKind kind, string source, string text)
        {
            var scroller = FindScrollViewer(ConsoleList);
            bool atBottom = scroller == null || scroller.VerticalOffset >= scroller.ScrollableHeight - 4;

            var line = new ConsoleLine(DateTime.Now, kind, source, text);
            _lines.Add(line);
            while (_lines.Count > NetworkToolsManager.MaxConsoleLines) _lines.RemoveAt(0);

            TxtLastActivity.Text = $"Last activity {line.TimeText}";
            ConsoleEmpty.Visibility = Visibility.Collapsed;
            if (IsCollapsed) IsCollapsed = false;   // something new to read: open it
            if (atBottom) ConsoleList.ScrollIntoView(line);
        }

        public void Clear()
        {
            _lines.Clear();
            TxtLastActivity.SetResourceReference(TextBlock.TextProperty, "Str_Network_NoActivityYet");
            ConsoleEmpty.Visibility = Visibility.Visible;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is ScrollViewer found) return found;
            return null;
        }

        private static string Text(IEnumerable<ConsoleLine> lines) => string.Join(Environment.NewLine, lines.Select(l => l.ToString()));

        private void BtnClear_Click(object sender, RoutedEventArgs e) => Clear();

        private void BtnCopy_Click(object sender, RoutedEventArgs e) => Copy();

        private void ConsoleList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Copy();
                e.Handled = true;
            }
        }

        private void Copy()
        {
            var selected = ConsoleList.SelectedItems.Cast<ConsoleLine>().OrderBy(l => _lines.IndexOf(l)).ToList();
            var lines = selected.Count > 0 ? selected : _lines.ToList();
            if (lines.Count == 0) return;
            try
            {
                Clipboard.SetText(Text(lines));
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Smart_Tools.WPF.Log.Info($"Clipboard busy: {ex.Message}");
            }
        }

        /// <summary>Dated name; a taken name gets " (2)" - a saved log never replaces another.</summary>
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_lines.Count == 0) return;
            var dialog = new SaveFileDialog
            {
                Title = "Save console",
                Filter = "Text file (*.txt)|*.txt|Log file (*.log)|*.log",
                FileName = $"{FileNamePrefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.txt",
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog() != true) return;
            string path = OutputFolderManager.UniquePath(dialog.FileName);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(Text(_lines) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log(ConsoleKind.Error, "LOG", "Couldn't save: " + ex.Message);
            }
        }
    }
}
