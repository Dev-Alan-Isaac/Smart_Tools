using Smart_Tools.WPF.Controllers;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
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
            _nanoLength = s.NanoLength;
            _shortLength = s.ShortLength;
            SelectByTag(CmbKind, s.Kind.ToString());
            SelectByTag(CmbWrap, s.Wrap.ToString());
            SelectByTag(CmbSeparator, s.Separator.ToString());
            TxtPrefix.Text = s.Prefix;
            TxtSuffix.Text = s.Suffix;
            TxtLength.Text = (s.Kind == IdKind.ShortCode ? _shortLength : _nanoLength).ToString(CultureInfo.InvariantCulture);
            _lengthKind = s.Kind;
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

        private int _nanoLength = 21, _shortLength = 8;
        private IdKind _lengthKind = IdKind.NanoId;   // which kind the length box is currently showing

        private static void SelectByTag(ComboBox box, string tag) =>
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == tag) ?? box.Items[0];

        private static T TagOf<T>(ComboBox box, T fallback) where T : struct, Enum =>
            (box.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse(tag, out T value) ? value : fallback;

        private IdGeneratorSettings ReadSettings()
        {
            int Num(string text, int fallback) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : fallback;
            var kind = TagOf(CmbKind, IdKind.Uuid4);
            // The length box belongs to NanoID or to Short code; keep each one's own value.
            if (kind == _lengthKind)
            {
                if (kind == IdKind.ShortCode) _shortLength = Num(TxtLength.Text, _shortLength);
                else if (kind == IdKind.NanoId) _nanoLength = Num(TxtLength.Text, _nanoLength);
            }
            return IdGeneratorManager.Clamp(new IdGeneratorSettings
            {
                Kind = kind,
                NanoLength = _nanoLength,
                ShortLength = _shortLength,
                Prefix = TxtPrefix.Text,
                Suffix = TxtSuffix.Text,
                Separator = TagOf(CmbSeparator, IdSeparator.NewLine),
                Wrap = TagOf(CmbWrap, IdWrap.Plain),
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
            bool lorem = kind == IdKind.Lorem;
            PanelCount.Visibility = lorem ? Visibility.Collapsed : Visibility.Visible;
            PanelLength.Visibility = kind is IdKind.NanoId or IdKind.ShortCode ? Visibility.Visible : Visibility.Collapsed;
            if (kind is IdKind.NanoId or IdKind.ShortCode && kind != _lengthKind)
            {
                _lengthKind = kind;
                bool was = _applying;
                _applying = true;
                TxtLength.Text = (kind == IdKind.ShortCode ? _shortLength : _nanoLength).ToString(CultureInfo.InvariantCulture);
                _applying = was;
            }
            PanelFormat.Visibility = lorem ? Visibility.Collapsed : Visibility.Visible;
            PanelSeparator.Visibility = ReadWrap() == IdWrap.Plain ? Visibility.Visible : Visibility.Collapsed;
            PanelHex.Visibility = kind == IdKind.Hex ? Visibility.Visible : Visibility.Collapsed;
            PanelLorem.Visibility = kind == IdKind.Lorem ? Visibility.Visible : Visibility.Collapsed;
            PanelCase.Visibility = kind is IdKind.Uuid4 or IdKind.Uuid7 or IdKind.Ulid or IdKind.Hex ? Visibility.Visible : Visibility.Collapsed;
            ChkNoHyphens.Visibility = kind is IdKind.Uuid4 or IdKind.Uuid7 ? Visibility.Visible : Visibility.Collapsed;
            TxtKindHint.Text = kind switch
            {
                IdKind.Uuid4 => Loc.T("Str_IdGen_HintUuid4", "128 random bits, the usual choice for a unique ID."),
                IdKind.Uuid7 => Loc.T("Str_IdGen_HintUuid7", "Starts with the time, so new IDs sort after older ones - good for database keys."),
                IdKind.Ulid => Loc.T("Str_IdGen_HintUlid", "26 characters, sorts by time, easy to read aloud."),
                IdKind.NanoId => Loc.T("Str_IdGen_HintNano", "Short, URL-safe and random (A-Z, a-z, 0-9, _ and -). 21 characters is as unique as a UUID."),
                IdKind.ShortCode => Loc.T("Str_IdGen_HintShort", "Short codes to read out or type: no 0, O, 1, I or L. Good for invites and tickets."),
                IdKind.Hex => Loc.T("Str_IdGen_HintHex", "Random bytes written as hexadecimal - handy for keys and tokens."),
                _ => Loc.T("Str_IdGen_HintLorem", "Placeholder text for layouts. It always starts with \"Lorem ipsum dolor sit amet\"."),
            };
        }

        private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            UpdatePanels();
            Generate();
            QueueSave();
        }

        private IdWrap ReadWrap() => TagOf(CmbWrap, IdWrap.Plain);

        private void Format_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
            TxtOutput.Text = IdGeneratorManager.Format(_lines, s);
            TxtInfo.Text = s.Kind == IdKind.Lorem
                ? Loc.N(_lines.Count, "Str_IdGen_MsgParagraphOne", "{0} paragraph", "Str_IdGen_MsgParagraphMany", "{0} paragraphs", _lines.Count)
                : Loc.N(_lines.Count, "Str_IdGen_MsgValueOne", "{0} value", "Str_IdGen_MsgValueMany", "{0} values", _lines.Count);
        }

        // ---- inspect ----

        private void Inspect_TextChanged(object sender, TextChangedEventArgs e)
        {
            var result = IdGeneratorManager.Inspect(TxtInspect.Text);
            PanelInspectResult.Visibility = result == null ? Visibility.Collapsed : Visibility.Visible;
            if (result == null) return;
            TxtInspectTitle.Text = result.Title;
            TxtInspectLines.Text = string.Join(Environment.NewLine, result.Lines);
        }

        private void BtnInspectPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText()) TxtInspect.Text = Clipboard.GetText().Trim();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                TxtInfo.Text = Loc.T("Str_IdGen_MsgClipboardBusy", "The clipboard is busy - try again");
            }
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;
            TxtInfo.Text = JsonSettings.TryCopy(TxtOutput.Text) ? Loc.T("Str_IdGen_MsgCopied", "Copied") : Loc.T("Str_IdGen_MsgClipboardBusy", "The clipboard is busy - try again");
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("Str_IdGen_MsgSaveTitle", "Save"), Filter = Loc.T("Str_IdGen_MsgSaveFilter", "Text file|*.txt"), FileName = "generated.txt", OverwritePrompt = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                AtomicFile.WriteAllText(dialog.FileName, TxtOutput.Text);
                TxtInfo.Text = Loc.F("Str_Generators_MsgSaved", "Saved {0}", Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtInfo.Text = Loc.F("Str_IdGen_MsgCantSave", "Couldn't save: {0}", ex.Message);
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
