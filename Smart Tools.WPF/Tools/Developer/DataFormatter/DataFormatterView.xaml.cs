using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// JSON / YAML / XML Formatter - UI only. Reading, formatting, and converting are in <see cref="DataFormatterManager"/>.
    /// The output updates as you type (after a short pause), off the UI thread so a big file doesn't freeze the window.
    /// </summary>
    public partial class DataFormatterView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;
        private const long MaxFileBytes = 20 * 1024 * 1024;
        private static string DialogTitle => Loc.T("Str_Tool_data-formatter_Name", "JSON / YAML / XML Formatter");

        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly bool _viewConstructed;
        private DataFormatKind? _from;   // null = detect from the text
        private DataFormatKind _to = DataFormatKind.Json;
        private DataFormatterAction _action = DataFormatterAction.Pretty;
        private DataFormatterIndent _indent = DataFormatterIndent.TwoSpaces;
        private int _generation;
        private bool _optionsCollapsed;
        private bool _loadedOnce;

        public DataFormatterView()
        {
            InitializeComponent();
            FileDrop.Attach(this, LoadFile);
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                _ = RunAsync();
            };
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: drops any result that is still being worked out.</summary>
        public void CancelActiveRun()
        {
            _debounce.Stop();
            _generation++;
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            UpdateOptionStates();
            ShowIdle();
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? OptionsCollapsedWidth : OptionsExpandedWidth);
        }

        // =================================================================
        //  Options
        // =================================================================

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || sender is not RadioButton { Tag: string tag } button) return;
            switch (button.GroupName)
            {
                case "FormatterFrom": _from = tag == "Auto" ? null : Enum.Parse<DataFormatKind>(tag); break;
                case "FormatterTo": _to = Enum.Parse<DataFormatKind>(tag); break;
                case "FormatterAction": _action = Enum.Parse<DataFormatterAction>(tag); break;
                case "FormatterIndent": _indent = Enum.Parse<DataFormatterIndent>(tag); break;
            }
            UpdateOptionStates();
            _ = RunAsync();
        }

        /// <summary>YAML has no minified form and its indent is fixed, so those choices are off while YAML is the output.</summary>
        private void UpdateOptionStates()
        {
            bool yaml = _to == DataFormatKind.Yaml;
            ActionRow.IsEnabled = !yaml;
            IndentRow.IsEnabled = !yaml && _action == DataFormatterAction.Pretty;
            TxtOptionNote.Text = yaml ? T("Str_Formatter_YamlNote", "YAML is always written in its readable block form, indented by two spaces.") : "";
        }

        // =================================================================
        //  Input
        // =================================================================

        private void TxtInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText()) TxtInput.Text = Clipboard.GetText();
            }
            catch (ExternalException)
            {
                // Another program holds the clipboard; the user can try again.
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e) => TxtInput.Clear();

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var open = new OpenFileDialog
            {
                Title = T("Str_Formatter_Open", "Open a file"),
                Filter = "JSON, YAML, XML|*.json;*.yaml;*.yml;*.xml|All files (*.*)|*.*",
            };
            if (open.ShowDialog() != true) return;
            LoadFile(open.FileName);
        }

        /// <summary>Reads a file into the input box (from the Open button or a drop).</summary>
        private void LoadFile(string path)
        {
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    MessageBox.Show(T("Str_Formatter_TooBig", "That file is larger than 20 MB, which is too big to format here."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                TxtInput.Text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // =================================================================
        //  Run
        // =================================================================

        private async Task RunAsync()
        {
            int generation = ++_generation;
            string text = TxtInput.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowIdle();
                return;
            }

            var from = _from ?? DataFormatterManager.Detect(text);
            var (to, action, indent) = (_to, _action, _indent);
            var result = await Task.Run(() => DataFormatterManager.Run(text, from, to, action, indent));
            if (generation != _generation) return;   // newer text is already on its way

            if (result.Ok) ShowResult(result, to, text.Length);
            else ShowError(result);
        }

        private void ShowIdle()
        {
            TxtOutput.Clear();
            SetOutputButtons(false);
            TxtResult.Text = T("Str_Formatter_Idle", "Waiting for data");
            TxtResultDetail.Text = T("Str_Formatter_IdleDetail", "Paste or open JSON, YAML, or XML on the left.");
            TxtSize.Text = "";
            SetPill("Info", PackIconKind.TextBoxOutline);
        }

        private void ShowResult(DataFormatterResult result, DataFormatKind to, int inputLength)
        {
            TxtOutput.Text = result.Text;
            SetOutputButtons(true);
            string from = KindName(result.Source);
            TxtResult.Text = string.Format(T("Str_Formatter_Valid", "Valid {0}"), from);
            TxtResultDetail.Text = result.Source == to
                ? T(_action == DataFormatterAction.Minify ? "Str_Formatter_DetailMinified" : "Str_Formatter_DetailFormatted", _action == DataFormatterAction.Minify ? "Minified" : "Formatted")
                : string.Format(T("Str_Formatter_DetailConverted", "Converted to {0}"), KindName(to));
            TxtSize.Text = string.Format(T("Str_Formatter_Size", "{0:N0} → {1:N0} characters"), inputLength, result.Text.Length);
            SetPill("Success", PackIconKind.CheckCircleOutline);
        }

        private void ShowError(DataFormatterResult result)
        {
            TxtOutput.Clear();
            SetOutputButtons(false);
            TxtResult.Text = string.Format(T("Str_Formatter_Invalid", "Invalid {0}"), KindName(result.Source));
            string where = result.Line > 0
                ? string.Format(T("Str_Formatter_At", "Line {0}, column {1}: "), result.Line, Math.Max(result.Column, 1))
                : "";
            TxtResultDetail.Text = where + result.Message;
            TxtResultDetail.ToolTip = TxtResultDetail.Text;
            TxtSize.Text = "";
            SetPill("Error", PackIconKind.AlertCircleOutline);
        }

        private static string KindName(DataFormatKind kind) => kind.ToString().ToUpperInvariant();

        private void SetPill(string kind, PackIconKind icon)
        {
            PillResult.SetResourceReference(Border.BackgroundProperty, $"Status{kind}Bg");
            IconResult.SetResourceReference(PackIcon.ForegroundProperty, $"Status{kind}Fg");
            TxtResult.SetResourceReference(TextBlock.ForegroundProperty, $"Status{kind}Fg");
            IconResult.Kind = icon;
        }

        private void SetOutputButtons(bool on)
        {
            BtnCopyOutput.IsEnabled = on;
            BtnSaveOutput.IsEnabled = on;
            BtnUseAsInput.IsEnabled = on;
        }

        // =================================================================
        //  Output actions
        // =================================================================

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;
            try
            {
                Clipboard.SetText(TxtOutput.Text);
            }
            catch (ExternalException)
            {
                return;
            }
            IconCopy.Kind = PackIconKind.Check;
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                IconCopy.Kind = PackIconKind.ContentCopy;
            };
            reset.Start();
        }

        /// <summary>Moves the output into the input, so a result can be converted or formatted again.</summary>
        private void BtnUseAsInput_Click(object sender, RoutedEventArgs e)
        {
            string text = TxtOutput.Text;
            if (text.Length == 0) return;
            _from = null;
            FromAuto.IsChecked = true;
            TxtInput.Text = text;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;
            string ext = _to switch { DataFormatKind.Yaml => "yaml", DataFormatKind.Xml => "xml", _ => "json" };
            var save = new SaveFileDialog { Title = T("Str_Formatter_Save", "Save the output"), Filter = $"{KindName(_to)} (*.{ext})|*.{ext}", FileName = $"output.{ext}" };
            if (save.ShowDialog() != true) return;
            try
            {
                File.WriteAllText(save.FileName, TxtOutput.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
