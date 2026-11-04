using Smart_Tools.WPF.Controllers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>CSV / JSON Converter - UI only. Reading and writing both formats is <see cref="CsvJsonManager"/>.</summary>
    public partial class CsvJsonView : UserControl
    {
        private readonly DispatcherTimer _convertTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly bool _viewConstructed;
        private bool _loadedOnce;
        private bool _applying;
        private int _run;
        private CsvJsonDirection _direction = CsvJsonDirection.CsvToJson;

        public CsvJsonView()
        {
            InitializeComponent();
            _convertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _convertTimer.Tick += (_, _) => { _convertTimer.Stop(); _ = ConvertAsync(); };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _viewConstructed = true;
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            var s = JsonSettings.Load<CsvJsonSettings>(CsvJsonManager.SettingsName);
            _applying = true;
            _direction = Enum.IsDefined(s.Direction) ? s.Direction : CsvJsonDirection.CsvToJson;
            (_direction == CsvJsonDirection.JsonToCsv ? RadioJsonToCsv : RadioCsvToJson).IsChecked = true;
            foreach (ComboBoxItem item in CmbDelimiter.Items)
                if ((string)item.Tag == s.Delimiter) CmbDelimiter.SelectedItem = item;
            if (CmbDelimiter.SelectedIndex < 0) CmbDelimiter.SelectedIndex = 0;
            ChkHeader.IsChecked = s.HasHeader;
            ChkTypes.IsChecked = s.DetectTypes;
            ChkIndented.IsChecked = s.Indented;
            _applying = false;
            UpdateLabels();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private CsvJsonSettings ReadSettings() => new()
        {
            Direction = _direction,
            Delimiter = (CmbDelimiter.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto",
            HasHeader = ChkHeader.IsChecked == true,
            DetectTypes = ChkTypes.IsChecked == true,
            Indented = ChkIndented.IsChecked == true,
        };

        private void SaveSettings() => JsonSettings.Save(CsvJsonManager.SettingsName, ReadSettings());

        private void Changed()
        {
            if (!_viewConstructed || _applying) return;
            _convertTimer.Stop();
            _convertTimer.Start();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Direction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse(tag, out CsvJsonDirection dir) || dir == _direction) return;
            _direction = dir;
            // What was the result becomes the new input, so a round trip is one click.
            if (TxtOutput.Text.Length > 0 && ErrorBanner.Visibility != Visibility.Visible)
            {
                _applying = true;
                TxtInput.Text = TxtOutput.Text;
                _applying = false;
            }
            UpdateLabels();
            Changed();
        }

        private void UpdateLabels()
        {
            bool toJson = _direction == CsvJsonDirection.CsvToJson;
            TxtInputTitle.Content = toJson
                ? Loc.T("Str_CsvJson_InTitleCsv", "CSV (paste, drop a file, or open one)")
                : Loc.T("Str_CsvJson_InTitleJson", "JSON (paste, drop a file, or open one)");
            TxtOutputTitle.Content = toJson ? "JSON" : "CSV";
            PanelCsvOptions.Visibility = Visibility.Visible;
            ChkHeader.Visibility = ChkTypes.Visibility = ChkIndented.Visibility = toJson ? Visibility.Visible : Visibility.Collapsed;
            TxtHint.Text = toJson
                ? Loc.T("Str_CsvJson_HintToJson", "Numbers like 007 stay text so IDs and zip codes keep their zeros.")
                : Loc.T("Str_CsvJson_HintToCsv", "A JSON list of objects becomes one row each; the columns are every key found. Nested values are written as JSON text.");
            _ = ConvertAsync();
        }

        private void Option_Click(object sender, RoutedEventArgs e) => Changed();
        private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e) => Changed();
        private void Input_TextChanged(object sender, TextChangedEventArgs e) => Changed();

        private async Task ConvertAsync()
        {
            int run = ++_run;
            string input = TxtInput.Text;
            var s = ReadSettings();
            var result = await Task.Run(() =>
            {
                char delimiter = CsvJsonManager.DelimiterOf(s.Delimiter, s.Direction == CsvJsonDirection.CsvToJson ? input : "");
                return s.Direction == CsvJsonDirection.CsvToJson
                    ? CsvJsonManager.CsvToJson(input, delimiter, s.HasHeader, s.DetectTypes, s.Indented)
                    : CsvJsonManager.JsonToCsv(input, s.Delimiter == "Auto" ? ',' : delimiter);
            });
            if (run != _run) return;

            if (result.Error != null)
            {
                TxtError.Text = result.Error;
                ErrorBanner.Visibility = Visibility.Visible;
                TxtOutput.Text = "";
                TxtInfo.Text = "Can't convert yet";
            }
            else
            {
                ErrorBanner.Visibility = Visibility.Collapsed;
                TxtOutput.Text = result.Text;
                TxtInfo.Text = result.Rows > 0
                    ? string.Format(CultureInfo.CurrentCulture, "{0:N0} row{1} · {2:N0} column{3}", result.Rows, result.Rows == 1 ? "" : "s", result.Columns, result.Columns == 1 ? "" : "s")
                    : "Paste or open something to convert";
            }
            BtnCopy.IsEnabled = BtnSave.IsEnabled = TxtOutput.Text.Length > 0;
        }

        // =================================================================
        //  Files, clipboard
        // =================================================================

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            bool toJson = _direction == CsvJsonDirection.CsvToJson;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("Str_Tool_csv-json_Name", "CSV / JSON Converter"),
                Filter = (toJson ? Loc.T("Str_CsvJson_FilterCsv", "CSV and text") + "|*.csv;*.tsv;*.txt" : "JSON|*.json;*.txt")
                    + "|" + Loc.T("Str_CsvJson_AllFiles", "All files") + "|*.*",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) LoadFile(dialog.FileName);
        }

        private void LoadFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > CsvJsonManager.MaxChars) { Fail(Loc.T("Str_CsvJson_TooBig", "That file is too big for this tool (over 20 MB).")); return; }
                TxtInput.Text = ReadText(path);
                if (_direction == CsvJsonDirection.CsvToJson && string.Equals(Path.GetExtension(path), ".tsv", StringComparison.OrdinalIgnoreCase))
                    CmbDelimiter.SelectedIndex = 3;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(Loc.F("Str_CsvJson_ReadFailed", "Couldn't read the file: {0}", ex.Message));
            }
        }

        /// <summary>
        /// UTF-8 (with or without a BOM) or UTF-16 as before; a file that is not valid UTF-8 (an Excel "CSV" saved in
        /// Windows-1252) is read as Windows-1252 instead of turning every accent into a replacement character.
        /// </summary>
        private static string ReadText(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF)) return File.ReadAllText(path);
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
        }

        private void Fail(string message)
        {
            TxtError.Text = message;
            ErrorBanner.Visibility = Visibility.Visible;
        }

        private void Input_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        private void Input_PreviewDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]))
            {
                LoadFile(files[0]);
                e.Handled = true;
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            TxtInput.Clear();
            TxtOutput.Clear();
            ErrorBanner.Visibility = Visibility.Collapsed;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;
            TxtInfo.Text = JsonSettings.TryCopy(TxtOutput.Text) ? Loc.T("Str_CsvJson_Copied", "Copied") : Loc.T("Str_CsvJson_ClipboardBusy", "The clipboard is busy - try again");
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;
            bool toJson = _direction == CsvJsonDirection.CsvToJson;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.T("Str_Tool_csv-json_Name", "CSV / JSON Converter"),
                Filter = toJson ? "JSON|*.json" : "CSV|*.csv",
                FileName = toJson ? "converted.json" : "converted.csv",
                OverwritePrompt = false,   // the app never overwrites: a taken name becomes "name (2)"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                string target = SafeFileOps.UniquePath(dialog.FileName);
                AtomicFile.WriteAllText(target, TxtOutput.Text, new UTF8Encoding(!toJson));   // CSV with a BOM so Excel reads accents
                TxtInfo.Text = Loc.F("Str_CsvJson_Saved", "Saved {0}", Path.GetFileName(target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(Loc.F("Str_CsvJson_SaveFailed", "Couldn't save: {0}", ex.Message));
            }
        }

        public void CancelActiveRun()
        {
            _run++;
            _convertTimer.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
