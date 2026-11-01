using Smart_Tools.WPF.Controllers;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>ID &amp; Lorem Generator - UI only. Everything is made by <see cref="IdGeneratorManager"/>.</summary>
    public partial class IdGeneratorView : UserControl
    {
        private readonly DispatcherTimer _saveTimer;
        private readonly bool _viewConstructed;
        private bool _loadedOnce;
        private bool _applying;
        private List<string> _lines = new();

        public IdGeneratorView()
        {
            InitializeComponent();
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _viewConstructed = true;
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            var s = IdGeneratorManager.Clamp(JsonSettings.Load<IdGeneratorSettings>(IdGeneratorManager.SettingsName));
            _applying = true;
            CmbKind.SelectedIndex = (int)s.Kind;
            TxtCount.Text = s.Count.ToString(CultureInfo.InvariantCulture);
            TxtHexBytes.Text = s.HexBytes.ToString(CultureInfo.InvariantCulture);
            TxtParagraphs.Text = s.LoremParagraphs.ToString(CultureInfo.InvariantCulture);
            ChkUpper.IsChecked = s.Uppercase;
            ChkNoHyphens.IsChecked = s.NoHyphens;
            _applying = false;
            UpdatePanels();
            Generate();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private IdGeneratorSettings ReadSettings()
        {
            int Num(string text, int fallback) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : fallback;
            return IdGeneratorManager.Clamp(new IdGeneratorSettings
            {
                Kind = CmbKind.SelectedIndex >= 0 ? (IdKind)CmbKind.SelectedIndex : IdKind.Uuid4,
                Count = Num(TxtCount.Text, 10),
                HexBytes = Num(TxtHexBytes.Text, 16),
                LoremParagraphs = Num(TxtParagraphs.Text, 3),
                Uppercase = ChkUpper.IsChecked == true,
                NoHyphens = ChkNoHyphens.IsChecked == true,
            });
        }

        private void SaveSettings() => JsonSettings.Save(IdGeneratorManager.SettingsName, ReadSettings());

        private void QueueSave()
        {
            if (!_viewConstructed || _applying) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void UpdatePanels()
        {
            var kind = ReadSettings().Kind;
            PanelCount.Visibility = kind == IdKind.Lorem ? Visibility.Collapsed : Visibility.Visible;
            PanelHex.Visibility = kind == IdKind.Hex ? Visibility.Visible : Visibility.Collapsed;
            PanelLorem.Visibility = kind == IdKind.Lorem ? Visibility.Visible : Visibility.Collapsed;
            PanelCase.Visibility = kind == IdKind.Lorem ? Visibility.Collapsed : Visibility.Visible;
            ChkNoHyphens.Visibility = kind is IdKind.Uuid4 or IdKind.Uuid7 ? Visibility.Visible : Visibility.Collapsed;
            TxtKindHint.Text = kind switch
            {
                IdKind.Uuid4 => "128 random bits, the usual choice for a unique ID.",
                IdKind.Uuid7 => "Starts with the time, so new IDs sort after older ones - good for database keys.",
                IdKind.Ulid => "26 characters, sorts by time, easy to read aloud.",
                IdKind.Hex => "Random bytes written as hexadecimal - handy for keys and tokens.",
                _ => "Placeholder text for layouts. It always starts with \"Lorem ipsum dolor sit amet\".",
            };
        }

        private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            UpdatePanels();
            Generate();
            QueueSave();
        }

        private void Option_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            Generate();
            QueueSave();
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            Generate();
            QueueSave();
        }

        private void BtnGenerate_Click(object sender, RoutedEventArgs e) => Generate();

        private void Generate()
        {
            var s = ReadSettings();
            _lines = IdGeneratorManager.Generate(s);
            string separator = s.Kind == IdKind.Lorem ? Environment.NewLine + Environment.NewLine : Environment.NewLine;
            TxtOutput.Text = string.Join(separator, _lines);
            TxtInfo.Text = s.Kind == IdKind.Lorem
                ? $"{_lines.Count} paragraph{(_lines.Count == 1 ? "" : "s")}"
                : $"{_lines.Count} value{(_lines.Count == 1 ? "" : "s")}";
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;
            TxtInfo.Text = JsonSettings.TryCopy(TxtOutput.Text) ? "Copied" : "The clipboard is busy - try again";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save", Filter = "Text file|*.txt", FileName = "generated.txt", OverwritePrompt = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                File.WriteAllText(dialog.FileName, TxtOutput.Text);
                TxtInfo.Text = "Saved " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtInfo.Text = "Couldn't save: " + ex.Message;
            }
        }

        public void CancelActiveRun()
        {
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
