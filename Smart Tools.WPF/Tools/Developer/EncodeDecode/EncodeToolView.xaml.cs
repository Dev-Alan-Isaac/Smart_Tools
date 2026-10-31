using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Base64 &amp; URL Encoder / Decoder - UI only. The conversions, the file reading and the
    /// settings are in <see cref="EncodeToolManager"/>. Four modes share one card: Base64, URL
    /// (percent encoding), HTML entities and files to and from Base64 (data: URIs).
    /// What is typed and the chosen file are never saved; only the setup.
    /// </summary>
    public partial class EncodeToolView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        /// <summary>The boxes show at most this much; Copy and Save always use the whole result.</summary>
        private const int MaxShownChars = 200_000;

        /// <summary>Below this the conversion runs on the UI thread (it takes microseconds); above it, on a worker.</summary>
        private const int InlineLimit = 20_000;

        private readonly DispatcherTimer _convertTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _pillTimer;
        private readonly bool _viewConstructed;

        private EncodeToolMode _mode = EncodeToolMode.Base64;
        private EncodeToolDirection _direction = EncodeToolDirection.Encode;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private int _runId;

        private byte[]? _fileBytes;                 // the file chosen in File mode; session only, never saved
        private string _fileName = "";
        private EncodeToolResult? _result;
        private string _fullOutput = "";

        private enum StatusKind { Error, Success, Warning }

        public EncodeToolView()
        {
            InitializeComponent();

            _convertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _convertTimer.Tick += (_, _) =>
            {
                _convertTimer.Stop();
                RunConvert();
            };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettings();
            };
            _pillTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            _pillTimer.Tick += (_, _) =>
            {
                _pillTimer.Stop();
                UiMotion.FadeVisibility(CopiedPill, false);
            };

            _viewConstructed = true;
        }

        // =================================================================
        //  Start-up and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ApplySettings(EncodeToolManager.LoadSettings());
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            // The view is kept alive by the shell; nothing runs while it is away except a pending save.
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SaveSettings();
            }
        }

        private void ApplySettings(EncodeToolSettings s)
        {
            _applyingSettings = true;
            try
            {
                SelectTag(CmbTextEncoding, s.TextEncoding.ToString());
                (s.UrlSafeAlphabet == true ? RadioAlphaUrl : RadioAlphaStd).IsChecked = true;
                ChkPadding.IsChecked = s.Padding ?? true;
                ChkWrap.IsChecked = s.WrapLines ?? false;
                (s.UrlScope == EncodeToolUrlScope.FullUrl ? RadioScopeFull : RadioScopeComponent).IsChecked = true;
                ChkSpaceAsPlus.IsChecked = s.SpaceAsPlus ?? false;
                ChkHtmlAll.IsChecked = s.HtmlAllNonAscii ?? false;
                ChkDataUri.IsChecked = s.DataUri ?? false;
                ChkShowHex.IsChecked = s.ShowHex ?? false;
                _direction = s.Direction;
                if (s.OptionsCollapsed != _optionsCollapsed) BtnCollapseOptions_Click(this, new RoutedEventArgs());
            }
            finally
            {
                _applyingSettings = false;
            }
            SetMode(s.Mode);
        }

        private EncodeToolOptions ReadOptions() => new()
        {
            Mode = _mode,
            Direction = _direction,
            TextEncoding = Enum.TryParse<EncodeToolTextEncoding>(SelectedTag(CmbTextEncoding), out var te) ? te : EncodeToolTextEncoding.Utf8,
            UrlSafeAlphabet = RadioAlphaUrl.IsChecked == true,
            Padding = ChkPadding.IsChecked == true,
            WrapLines = ChkWrap.IsChecked == true && !(ChkDataUri.IsChecked == true && _mode == EncodeToolMode.File),
            UrlScope = RadioScopeFull.IsChecked == true ? EncodeToolUrlScope.FullUrl : EncodeToolUrlScope.Component,
            SpaceAsPlus = ChkSpaceAsPlus.IsChecked == true,
            HtmlAllNonAscii = ChkHtmlAll.IsChecked == true,
            DataUri = ChkDataUri.IsChecked == true,
        };

        private EncodeToolSettings ReadSettings()
        {
            var o = ReadOptions();
            return new EncodeToolSettings
            {
                Mode = _mode,
                Direction = _direction,
                TextEncoding = o.TextEncoding,
                UrlSafeAlphabet = o.UrlSafeAlphabet,
                Padding = o.Padding,
                WrapLines = ChkWrap.IsChecked == true,
                UrlScope = o.UrlScope,
                SpaceAsPlus = o.SpaceAsPlus,
                HtmlAllNonAscii = o.HtmlAllNonAscii,
                DataUri = o.DataUri,
                ShowHex = ChkShowHex.IsChecked == true,
                OptionsCollapsed = _optionsCollapsed,
            };
        }

        private void SaveSettings() => EncodeToolManager.SaveSettings(ReadSettings());

        private void ScheduleSave()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void ScheduleConvert()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _convertTimer.Stop();
            _convertTimer.Start();
        }

        private string Str(string key, string fallback = "") => TryFindResource(key) as string ?? fallback;

        private static void SelectTag(ComboBox combo, string tag)
        {
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
                if ((item.Tag as string) == tag) { combo.SelectedItem = item; return; }
            combo.SelectedIndex = 0;
        }

        private static string SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        // =================================================================
        //  Modes and sidebar
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            ScheduleSave();
        }

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || (sender as RadioButton)?.Tag is not string tag) return;
            SetMode(Enum.TryParse<EncodeToolMode>(tag, out var mode) ? mode : EncodeToolMode.Base64);
            ScheduleSave();
        }

        private void Direction_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || (sender as RadioButton)?.Tag is not string tag) return;
            _direction = tag == "Decode" ? EncodeToolDirection.Decode : EncodeToolDirection.Encode;
            SetMode(_mode);
            ScheduleSave();
        }

        /// <summary>Shows the panels that belong to the mode and direction, then converts at once.</summary>
        private void SetMode(EncodeToolMode mode)
        {
            _mode = mode;
            bool encode = _direction == EncodeToolDirection.Encode;
            bool fileEncode = mode == EncodeToolMode.File && encode;

            (mode switch
            {
                EncodeToolMode.Url => RadioModeUrl,
                EncodeToolMode.Html => RadioModeHtml,
                EncodeToolMode.File => RadioModeFile,
                _ => RadioModeBase64,
            }).IsChecked = true;
            (encode ? RadioEncode : RadioDecode).IsChecked = true;

            static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
            TextEncodingPanel.Visibility = Show(mode is EncodeToolMode.Base64 or EncodeToolMode.Url || mode == EncodeToolMode.File && !encode);
            Base64OptionsPanel.Visibility = Show(mode is EncodeToolMode.Base64 or EncodeToolMode.File);
            UrlOptionsPanel.Visibility = Show(mode == EncodeToolMode.Url);
            HtmlOptionsPanel.Visibility = Show(mode == EncodeToolMode.Html);
            FileOptionsPanel.Visibility = Show(mode == EncodeToolMode.File);
            FileDropArea.Visibility = Show(fileEncode);
            InputSection.Visibility = Show(!fileEncode);

            // Settings that only apply when writing are greyed out when reading, with the reason in the hint.
            AlphabetChips.IsEnabled = ChkPadding.IsEnabled = encode;
            ChkWrap.IsEnabled = encode && !(mode == EncodeToolMode.File && ChkDataUri.IsChecked == true);
            ScopeChips.IsEnabled = encode;
            ChkHtmlAll.IsEnabled = encode;
            ChkDataUri.IsEnabled = fileEncode;
            TxtB64Hint.Text = Str(encode ? "Str_EncTool_B64EncodeHint" : "Str_EncTool_B64DecodeHint");
            TxtScopeHint.Text = Str(encode ? "Str_EncTool_ScopeEncodeHint" : "Str_EncTool_ScopeDecodeHint");
            TxtHtmlHint.Text = Str(encode ? "Str_EncTool_HtmlEncodeHint" : "Str_EncTool_HtmlDecodeHint");
            TxtFileHint.Text = Str(fileEncode ? "Str_EncTool_FileEncodeHint" : "Str_EncTool_FileDecodeHint");
            TxtOpenLabel.Text = Str(fileEncode ? "Str_EncTool_ChooseFile" : "Str_EncTool_OpenFile");

            var (titleKey, icon, factsKey) = mode switch
            {
                EncodeToolMode.Url => ("Str_EncTool_ModeUrl", PackIconKind.LinkVariant, "FactsUrl"),
                EncodeToolMode.Html => ("Str_EncTool_ModeHtml", PackIconKind.LanguageHtml5, "FactsHtml"),
                EncodeToolMode.File => ("Str_EncTool_ModeFile", PackIconKind.FileCodeOutline, "FactsFile"),
                _ => ("Str_EncTool_ModeBase64", PackIconKind.Numeric, "FactsBase64"),
            };
            HeaderTitle.SetResourceReference(ContentControl.ContentProperty, titleKey);
            HeaderTitle.Tag = icon;
            FactsHost.Content = FindResource(factsKey);
            UiMotion.FadeIn(HeaderTitle);

            TxtInput.Tag = Str(encode ? "Str_EncTool_InputHintEncode" : "Str_EncTool_InputHintDecode");
            UpdateInputCount();
            RunConvert();
        }

        /// <summary>When the card is too narrow for both groups, the Open / Clear buttons drop to a second row.</summary>
        private void DirectionRow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool narrow = DirectionRow.ActualWidth < 600;
            Grid.SetRow(DirectionActions, narrow ? 1 : 0);
            Grid.SetColumn(DirectionActions, narrow ? 0 : 2);
            Grid.SetColumnSpan(DirectionActions, narrow ? 3 : 1);
            DirectionActions.HorizontalAlignment = narrow ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
            DirectionActions.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            ChkWrap.IsEnabled = _direction == EncodeToolDirection.Encode && !(_mode == EncodeToolMode.File && ChkDataUri.IsChecked == true);
            ScheduleConvert();
            ScheduleSave();
        }

        private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            ScheduleConvert();
            ScheduleSave();
        }

        private void ChkShowHex_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateHex();
            ScheduleSave();
        }

        // =================================================================
        //  Converting
        // =================================================================

        private void TxtInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateInputCount();
            ScheduleConvert();
        }

        private void UpdateInputCount() =>
            TxtInputCount.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_EncTool_CharCount", "{0} characters"),
                TxtInput.Text.Length.ToString("N0", CultureInfo.CurrentCulture));

        private async void RunConvert()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _convertTimer.Stop();
            int id = ++_runId;
            var options = ReadOptions();
            try
            {
                EncodeToolResult? result;
                if (_mode == EncodeToolMode.File && _direction == EncodeToolDirection.Encode)
                {
                    if (_fileBytes == null) { ShowEmpty(); return; }
                    byte[] data = _fileBytes;
                    string name = _fileName;
                    result = await Task.Run(() => EncodeToolManager.EncodeBytes(data, name, options));
                }
                else
                {
                    string input = TxtInput.Text;
                    if (input.Length == 0) { ShowEmpty(); return; }
                    result = input.Length < InlineLimit
                        ? EncodeToolManager.Convert(input, options)
                        : await Task.Run(() => EncodeToolManager.Convert(input, options));
                }
                if (id != _runId) return;                       // a newer request overtook this one
                ApplyResult(result, options);
            }
            catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException)
            {
                if (id == _runId) ShowStatus(Str("Str_EncTool_Problem_TooLarge", "Too large."), StatusKind.Error);
            }
        }

        private void ShowEmpty()
        {
            _result = null;
            _fullOutput = "";
            StatusBanner.Visibility = Visibility.Collapsed;
            NotesList.ItemsSource = null;
            OutputSection.Visibility = Visibility.Collapsed;
            TxtOutput.Clear();
            TxtHex.Clear();
            TxtEmptyHint.Text = Str(_mode == EncodeToolMode.File && _direction == EncodeToolDirection.Encode
                ? "Str_EncTool_EmptyHintFile" : "Str_EncTool_EmptyHint");
            TxtEmptyHint.Visibility = Visibility.Visible;
            BtnSwap.IsEnabled = false;
        }

        private void ApplyResult(EncodeToolResult result, EncodeToolOptions options)
        {
            _result = result;
            _fullOutput = result.Output;
            TxtEmptyHint.Visibility = Visibility.Collapsed;

            if (!result.Ok)
            {
                OutputSection.Visibility = Visibility.Collapsed;
                NotesList.ItemsSource = null;
                BtnSwap.IsEnabled = false;
                string template = Str("Str_EncTool_Problem_" + result.ProblemCode, result.ProblemCode);
                ShowStatus(string.Format(CultureInfo.CurrentCulture, template, (result.ProblemIndex + 1).ToString("N0", CultureInfo.CurrentCulture), result.ProblemChar),
                    StatusKind.Error);
                return;
            }

            bool encode = options.Direction == EncodeToolDirection.Encode;
            bool binary = !result.IsText && result.Output.Length == 0;

            // Status: reading is checked, so say it was valid. Writing needs no comment.
            if (!encode)
            {
                string key = options.Mode switch { EncodeToolMode.Url => "Str_EncTool_Ok_Url", EncodeToolMode.Html => "Str_EncTool_Ok_Html", _ => "Str_EncTool_Ok_Base64" };
                ShowStatus(Str(key), StatusKind.Success);
            }
            else StatusBanner.Visibility = Visibility.Collapsed;

            // Output: text, or a file panel when the bytes are not text.
            OutputSection.Visibility = Visibility.Visible;
            var notes = new List<string>();
            if (binary)
            {
                TxtOutput.Visibility = Visibility.Collapsed;
                BinaryPanel.Visibility = Visibility.Visible;
                string size = FormatCount(result.OutputBytes, bytes: true);
                string kind = result.DetectedKind.Length > 0 ? Str("Str_EncTool_Kind_" + result.DetectedKind, result.DetectedKind) : "";
                TxtBinaryTitle.Text = kind.Length > 0
                    ? string.Format(CultureInfo.CurrentCulture, Str("Str_EncTool_BinaryKnown", "{0} · {1}"), kind, size)
                    : string.Format(CultureInfo.CurrentCulture, Str("Str_EncTool_BinaryUnknown", "Binary data · {0}"), size);
            }
            else
            {
                BinaryPanel.Visibility = Visibility.Collapsed;
                TxtOutput.Visibility = Visibility.Visible;
                if (_fullOutput.Length > MaxShownChars)
                {
                    TxtOutput.Text = _fullOutput[..MaxShownChars];
                    notes.Add(string.Format(CultureInfo.CurrentCulture, Str("Str_EncTool_Note_Truncated", "Showing the first {0} characters. Copy and Save use the whole result."),
                        MaxShownChars.ToString("N0", CultureInfo.CurrentCulture)));
                }
                else TxtOutput.Text = _fullOutput;
            }

            foreach (string code in result.Notes)
            {
                string text = Str("Str_EncTool_Note_" + code);
                if (text.Length > 0) notes.Add(text);
            }
            if (result.Mime.Length > 0 && !encode)
                notes.Add(string.Format(CultureInfo.CurrentCulture, Str("Str_EncTool_Note_Mime", "Type from the data: address: {0}"), result.Mime));
            NotesList.ItemsSource = notes.Count > 0 ? notes : null;

            UpdateStats(result, options);
            UpdateHex();
            BtnSwap.IsEnabled = result.IsText && _fullOutput.Length > 0 && !(options.Mode == EncodeToolMode.File && !encode);
            UiMotion.FadeIn(OutputSection);
        }

        private void UpdateStats(EncodeToolResult result, EncodeToolOptions options)
        {
            bool encode = options.Direction == EncodeToolDirection.Encode;
            bool html = options.Mode == EncodeToolMode.Html;
            // Writing starts from bytes and ends in characters; reading is the other way round. HTML is counted in bytes both ways.
            TxtPillIn.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_EncTool_StatIn", "Input: {0}"), FormatCount(result.InputBytes, bytes: encode || html));
            TxtPillOut.Text = string.Format(CultureInfo.CurrentCulture, Str("Str_EncTool_StatOut", "Output: {0}"), FormatCount(result.OutputBytes, bytes: !encode || html));
            PillIn.Visibility = PillOut.Visibility = Visibility.Visible;

            bool showRatio = encode && !html && result.InputBytes > 0;
            if (showRatio)
            {
                double change = (double)result.OutputBytes / result.InputBytes - 1;
                TxtPillRatio.Text = (change * 100).ToString("+0;-0;0", CultureInfo.CurrentCulture) + "%";
                UiMotion.Pop(PillRatio);
            }
            PillRatio.Visibility = showRatio ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateHex()
        {
            bool show = ChkShowHex.IsChecked == true && _result is { Ok: true, Bytes: { Length: > 0 } } && _mode != EncodeToolMode.Html;
            HexSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtHex.Text = show ? EncodeToolManager.HexDump(_result!.Bytes) : "";
        }

        private string FormatCount(long n, bool bytes)
        {
            string number = n.ToString("N0", CultureInfo.CurrentCulture);
            return string.Format(CultureInfo.CurrentCulture, Str(bytes ? "Str_EncTool_UnitBytes" : "Str_EncTool_UnitChars", "{0}"), number);
        }

        private void ShowStatus(string text, StatusKind kind)
        {
            var (bg, fg, icon) = kind switch
            {
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                StatusKind.Warning => ("StatusWarningBg", "StatusWarningFg", PackIconKind.AlertOutline),
                _ => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.CheckCircleOutline),
            };
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            StatusIcon.SetResourceReference(PackIcon.ForegroundProperty, fg);
            StatusIcon.Kind = icon;
            bool changed = TxtStatus.Text != text || StatusBanner.Visibility != Visibility.Visible;
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
            if (changed) UiMotion.FadeIn(StatusBanner);
        }

        // =================================================================
        //  Buttons: swap, open, clear, copy, save
        // =================================================================

        private void BtnSwap_Click(object sender, RoutedEventArgs e)
        {
            if (_result is not { Ok: true, IsText: true } || _fullOutput.Length == 0) return;
            string text = _fullOutput;
            _applyingSettings = true;                       // one conversion after both changes, not two
            try
            {
                TxtInput.Text = text;
                _direction = _direction == EncodeToolDirection.Encode ? EncodeToolDirection.Decode : EncodeToolDirection.Encode;
            }
            finally
            {
                _applyingSettings = false;
            }
            SetMode(_mode);
            ScheduleSave();
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            bool fileEncode = _mode == EncodeToolMode.File && _direction == EncodeToolDirection.Encode;
            var dialog = new OpenFileDialog
            {
                Title = Str(fileEncode ? "Str_EncTool_ChooseFile" : "Str_EncTool_OpenFile"),
                Filter = fileEncode
                    ? Str("Str_EncTool_FilterAll", "All files") + "|*.*"
                    : Str("Str_EncTool_FilterText", "Text files") + "|*.txt;*.b64;*.base64;*.json;*.xml;*.html;*.htm;*.csv;*.md;*.log;*.pem|" + Str("Str_EncTool_FilterAll", "All files") + "|*.*",
            };
            if (dialog.ShowDialog() != true) return;
            if (fileEncode) LoadFile(dialog.FileName);
            else LoadTextFile(dialog.FileName);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _applyingSettings = true;
            try
            {
                TxtInput.Clear();
                _fileBytes = null;
                _fileName = "";
            }
            finally
            {
                _applyingSettings = false;
            }
            ResetFileArea();
            RunConvert();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_fullOutput.Length == 0) return;
            try
            {
                Clipboard.SetText(_fullOutput);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                ShowStatus(Str("Str_EncTool_CopyFailed", "The clipboard is busy. Try again."), StatusKind.Warning);
                return;
            }
            ShowPill("Str_EncTool_Copied");
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_result is not { Ok: true } result) return;
            // Reading Base64 or percent-encoding saves the exact bytes; everything else saves the text.
            bool asBytes = _direction == EncodeToolDirection.Decode && result.Bytes != null;
            if (!asBytes && _fullOutput.Length == 0) return;

            string ext = asBytes ? EncodeToolManager.ExtensionForKind(result.DetectedKind) : ".txt";
            var dialog = new SaveFileDialog
            {
                Title = Str("Str_EncTool_SaveTooltip"),
                FileName = EncodeToolManager.SuggestFileName(result, asBytes),
                Filter = (ext.Length > 0 ? $"{ext.TrimStart('.').ToUpperInvariant()}|*{ext}|" : "")
                         + Str("Str_EncTool_FilterAll", "All files") + "|*.*",
            };
            if (dialog.ShowDialog() != true) return;

            string path = dialog.FileName;
            byte[]? data = asBytes ? result.Bytes : null;
            string text = _fullOutput;
            string? error = await Task.Run(() => data != null ? EncodeToolManager.SaveBytes(path, data) : EncodeToolManager.SaveText(path, text));
            if (error != null)
                ShowStatus(string.Format(CultureInfo.CurrentCulture, Str("Str_EncTool_SaveFailed", "Couldn't save: {0}"), error), StatusKind.Error);
            else
                ShowPill("Str_EncTool_Saved");
        }

        private void ShowPill(string textKey)
        {
            TxtPillText.Text = Str(textKey);
            CopiedPill.Visibility = Visibility.Visible;
            UiMotion.Pop(CopiedPill);
            _pillTimer.Stop();
            _pillTimer.Start();
        }

        // =================================================================
        //  Files: choose, drop, read
        // =================================================================

        private async void LoadFile(string path)
        {
            byte[]? data = null;
            string problem = "";
            await Task.Run(() => data = EncodeToolManager.ReadFile(path, out problem));
            if (data == null)
            {
                _fileBytes = null;
                ResetFileArea();
                ShowEmpty();
                ShowStatus(Str("Str_EncTool_Problem_" + problem, problem), StatusKind.Error);
                return;
            }
            _fileBytes = data;
            _fileName = Path.GetFileName(path);
            string kind = EncodeToolManager.DetectKind(data);
            TxtFileName.Text = _fileName;
            TxtFileInfo.Text = FormatSize(data.Length) + " · " + (kind.Length > 0 ? Str("Str_EncTool_Kind_" + kind, kind) : EncodeToolManager.MimeForFile(_fileName));
            FileBadge.Tag = PackIconKind.FileCheckOutline;
            UiMotion.FadeIn(FileDropArea);
            RunConvert();
        }

        private async void LoadTextFile(string path)
        {
            string? text = null;
            string problem = "";
            await Task.Run(() => text = EncodeToolManager.ReadTextFile(path, out problem));
            if (text == null)
            {
                ShowEmpty();
                ShowStatus(Str("Str_EncTool_Problem_" + problem, problem), StatusKind.Error);
                return;
            }
            TxtInput.Text = text;                           // TextChanged starts the conversion
        }

        private void ResetFileArea()
        {
            TxtFileName.Text = Str("Str_EncTool_DropFileTitle");
            TxtFileInfo.Text = Str("Str_EncTool_DropFileHint");
            FileBadge.Tag = PackIconKind.TrayArrowDown;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString("N0", CultureInfo.CurrentCulture) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
        }

        private static string? FirstDroppedFile(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0])
                ? files[0] : null;

        private void FileDropArea_DragOver(object sender, DragEventArgs e)
        {
            bool ok = FirstDroppedFile(e) != null;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) FileDropArea.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
            e.Handled = true;
        }

        private void FileDropArea_DragLeave(object sender, DragEventArgs e) =>
            FileDropArea.SetResourceReference(Border.BorderBrushProperty, "InputBorder");

        private void FileDropArea_Drop(object sender, DragEventArgs e)
        {
            FileDropArea.SetResourceReference(Border.BorderBrushProperty, "InputBorder");
            if (FirstDroppedFile(e) is { } path) LoadFile(path);
            e.Handled = true;
        }

        // A TextBox swallows file drags, so the preview events catch them first; dragged text still works.
        private void TxtInput_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = FirstDroppedFile(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void TxtInput_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (FirstDroppedFile(e) is { } path) LoadTextFile(path);
            e.Handled = true;
        }

        // =================================================================
        //  Shell hooks
        // =================================================================

        /// <summary>Called when the window closes: stops pending work and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _runId++;
            _convertTimer.Stop();
            _pillTimer.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
