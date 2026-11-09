using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Code Toolkit view - UI only. Minify / Beautify / Compare all run in
    /// <see cref="CodeToolkitManager"/> (off the UI thread, cancellable);
    /// data shapes live in CodeToolkitModel.cs.
    ///
    /// With "Update as you type" on, every edit re-runs ~350 ms after the
    /// last keystroke; a newer run cancels the one in flight, so results
    /// never arrive out of order.
    /// </summary>
    public partial class CodeToolkitView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        private readonly CodeToolkitManager _manager = new();
        private readonly DispatcherTimer _liveTimer;

        private CancellationTokenSource? _runCts;
        private CodeToolMode _mode = CodeToolMode.Minify;
        private CodeLanguage _language = CodeLanguage.Css;
        private CodeIndentStyle _indent = CodeIndentStyle.FourSpaces;

        private bool _optionsCollapsed;
        private bool _languageChosenByUser;   // stop auto-detect overriding a deliberate choice
        private bool _settingLanguage;        // programmatic change in progress
        private bool _outputIsStale;
        private string _lastLoadedBaseName = "output";

        private CodeFormatException? _lastError;
        private CodeDiffResult? _lastDiff;
        private int _changeCursor = -1;

        private readonly bool _viewConstructed;

        private enum StatusKind { Neutral, Busy, Success, Warning, Error }

        public CodeToolkitView()
        {
            InitializeComponent();
            EnsureFilesCard();

            _liveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(350),
            };
            _liveTimer.Tick += async (_, _) =>
            {
                _liveTimer.Stop();
                await RunAsync();
            };

            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _liveTimer.Stop();
            _runCts?.Cancel();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyMode(animate: false);
            UpdateInputInfo();
        }

        private bool IsLive => ChkLiveUpdate.IsChecked == true;

        // =================================================================
        //  Sidebar
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;

            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;

            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Mode / language / options
        // =================================================================

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;

            _mode = ModeBeautify.IsChecked == true ? CodeToolMode.Beautify
                  : ModeDiff.IsChecked == true ? CodeToolMode.Diff
                  : CodeToolMode.Minify;

            ApplyMode(animate: true);
            RunIfLive(immediately: true);
        }

        private void ApplyMode(bool animate)
        {
            bool diff = _mode == CodeToolMode.Diff;

            (string labelKey, PackIconKind icon, string hint) = _mode switch
            {
                CodeToolMode.Beautify => ("Str_Code_ModeBeautify", PackIconKind.AutoFix, "Re-indent and lay out code so it's easy to read."),
                CodeToolMode.Diff => ("Str_Code_ModeCompare", PackIconKind.CompareHorizontal, "See exactly which lines were added or removed between two versions."),
                _ => ("Str_Code_ModeMinify", PackIconKind.ArrowCollapseHorizontal, "Shrink code by removing comments and whitespace."),
            };

            TxtRunLabel.SetResourceReference(TextBlock.TextProperty, labelKey);
            IconRun.Kind = icon;
            TxtModeHint.Text = hint;

            void Show(UIElement el, bool visible)
            {
                if (animate) UiMotion.FadeVisibility(el, visible);
                else el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }

            Show(PanelFiles, !diff);
            Show(PanelTransform, !diff);
            Show(PanelDiff, diff);
            Show(LanguageSection, !diff);
            Show(BeautifyOptionsSection, _mode == CodeToolMode.Beautify);
            Show(MinifyInfoSection, _mode == CodeToolMode.Minify);
            Show(DiffOptionsSection, diff);

            UpdateMinifyNote();
            HideChips();
            HideError();
            TxtOutputEmpty.Text = _mode == CodeToolMode.Beautify ? "Formatted code appears here" : "Minified code appears here";

            if (diff)
                SetStatus(_lastDiff == null ? "Paste two versions to compare" : "Ready", StatusKind.Neutral);
            else if (TxtInput.Text.Length == 0)
                SetStatus("Paste code, drop a file or press Ctrl+O", StatusKind.Neutral);
        }

        private void Language_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || sender is not RadioButton { Tag: string tag } ||
                !Enum.TryParse(tag, out CodeLanguage language))
                return;

            _language = language;
            if (!_settingLanguage)
            {
                _languageChosenByUser = true;
                LanguageHintPanel.Visibility = Visibility.Collapsed;
            }

            UpdateMinifyNote();
            HideError();
            RunIfLive(immediately: true);
        }

        /// <summary>Selects a language from code (file extension / detection) and says why.</summary>
        private void SelectLanguage(CodeLanguage language, string reason)
        {
            RadioButton radio = language switch
            {
                CodeLanguage.Js => LangJs,
                CodeLanguage.Html => LangHtml,
                CodeLanguage.Json => LangJson,
                _ => LangCss,
            };

            _settingLanguage = true;
            try
            {
                if (radio.IsChecked != true)
                {
                    radio.IsChecked = true; // -> Language_Checked
                    UiMotion.Pop(radio);
                }
            }
            finally
            {
                _settingLanguage = false;
            }

            _language = language;
            TxtLanguageHint.Text = reason;
            UiMotion.FadeVisibility(LanguageHintPanel, true);
        }

        private void UpdateMinifyNote()
        {
            TxtMinifyNote.Text = _language switch
            {
                CodeLanguage.Js => "JavaScript: comments and extra spaces go, but line breaks that could end a statement are kept - always safe, a little less compact than a bundler.",
                CodeLanguage.Html => "HTML: comments and extra whitespace go; inline <style> and <script> are minified too. <pre> and <textarea> are left exactly as they are.",
                CodeLanguage.Json => "JSON: rewritten on one line. Comments and trailing commas in the input are accepted.",
                _ => "CSS: comments (except /*! licence */ ones), whitespace and final semicolons go.",
            };
        }

        private void Indent_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || sender is not RadioButton { Tag: string tag } ||
                !Enum.TryParse(tag, out CodeIndentStyle indent))
                return;

            _indent = indent;
            RunIfLive(immediately: true);
        }

        private void DiffOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            RunIfLive(immediately: true);
        }

        private void LiveUpdate_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (IsLive) RunIfLive(immediately: true);
            else _liveTimer.Stop();
        }

        // =================================================================
        //  Editing
        // =================================================================

        private void TxtInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;

            string text = TxtInput.Text;
            InputEmptyPanel.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateInputInfo();

            // Pasting into an empty box: guess the language (unless the person picked one).
            bool bigJump = e.Changes.Any(c => c.AddedLength > 20 && c.AddedLength >= text.Length - 2);
            if (bigJump && !_languageChosenByUser && CodeToolkitManager.DetectLanguage(text) is CodeLanguage detected)
                SelectLanguage(detected, "Detected from content");

            if (text.Length == 0)
            {
                ClearOutput();
                SetStatus("Paste code, drop a file or press Ctrl+O", StatusKind.Neutral);
                return;
            }

            if (IsLive && text.Length <= CodeToolkitManager.LiveUpdateMaxChars)
            {
                RunIfLive(immediately: false);
            }
            else if (TxtOutput.Text.Length > 0)
            {
                // Don't leave a result that silently no longer matches the input.
                MarkOutputStale(IsLive
                    ? "Large input - press Ctrl+Enter to update"
                    : "Input changed - press Ctrl+Enter to update");
            }
        }

        private void TxtDiff_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _mode != CodeToolMode.Diff) return;

            long total = TxtDiffOriginal.Text.Length + TxtDiffModified.Text.Length;
            if (IsLive && total <= CodeToolkitManager.LiveUpdateMaxChars)
                RunIfLive(immediately: false);
            else if (_lastDiff != null)
                SetStatus("Text changed - press Ctrl+Enter to compare again", StatusKind.Warning);
        }

        private void UpdateInputInfo()
        {
            string text = TxtInput.Text;
            TxtInputInfo.Text = text.Length == 0
                ? "Empty"
                : $"{CodeToolkitManager.CountLines(text):N0} lines · {text.Length:N0} characters";
        }

        // =================================================================
        //  Running
        // =================================================================

        private void RunIfLive(bool immediately)
        {
            if (!IsLive || !IsLoaded) return;

            _liveTimer.Stop();
            if (immediately)
                _ = RunAsync();
            else
                _liveTimer.Start();
        }

        private async void BtnRun_Click(object sender, RoutedEventArgs e) => await RunAsync();

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (ctrl && e.Key == Key.Enter)
            {
                e.Handled = true; // don't insert a newline in the editor
                _ = RunAsync();
            }
            else if (ctrl && e.Key == Key.O)
            {
                e.Handled = true;
                BtnLoadFile_Click(this, new RoutedEventArgs());
            }
            else if (e.Key == Key.F8 && _mode == CodeToolMode.Diff)
            {
                e.Handled = true;
                MoveToChange((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : +1);
            }
        }

        private async Task RunAsync()
        {
            _liveTimer.Stop();
            _runCts?.Cancel();
            var cts = new CancellationTokenSource();
            _runCts = cts;

            try
            {
                if (_mode == CodeToolMode.Diff)
                    await RunDiffAsync(cts.Token);
                else
                    await RunTransformAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer run
            }
            finally
            {
                if (ReferenceEquals(_runCts, cts)) _runCts = null;
                cts.Dispose();
            }
        }

        private async Task RunTransformAsync(CancellationToken token)
        {
            string input = TxtInput.Text;
            if (input.Trim().Length == 0)
            {
                ClearOutput();
                HideError();
                SetStatus("Nothing to run - paste or drop some code into Input first", StatusKind.Neutral);
                return;
            }

            CodeToolMode mode = _mode;
            CodeLanguage language = _language;
            string verb = mode == CodeToolMode.Minify ? "Minifying" : "Formatting";
            if (input.Length > 200_000) SetStatus($"{verb} {CodeToolkitManager.DisplayName(language)}…", StatusKind.Busy);

            CodeTransformResult result;
            try
            {
                result = await _manager.RunTransformAsync(input, mode, language, _indent, token);
            }
            catch (CodeFormatException ex)
            {
                if (token.IsCancellationRequested) return;
                ShowError(ex.Message, ex);
                ClearOutput();
                SetStatus(FirstLine(ex.Message), StatusKind.Error);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (token.IsCancellationRequested) return;
                ShowError($"{CodeToolkitManager.DisplayName(language)} couldn't be processed: {ex.Message}", null);
                ClearOutput();
                SetStatus("Something went wrong - see the details above", StatusKind.Error);
                return;
            }

            if (token.IsCancellationRequested) return;

            HideError();
            bool wasEmpty = TxtOutput.Text.Length == 0;
            TxtOutput.Text = result.Output;
            SetOutputOpacity(1);
            _outputIsStale = false;
            OutputEmptyPanel.Visibility = Visibility.Collapsed;
            BtnCopyOutput.IsEnabled = BtnSaveOutput.IsEnabled = BtnUseOutput.IsEnabled = result.Output.Length > 0;
            if (wasEmpty) UiMotion.FadeIn(TxtOutput, 4);

            string ms = result.Elapsed.TotalMilliseconds < 1 ? "<1 ms" : $"{result.Elapsed.TotalMilliseconds:0} ms";
            string name = CodeToolkitManager.DisplayName(language);
            SetStatus(mode == CodeToolMode.Minify ? $"{name} minified in {ms}" : $"{name} formatted in {ms}", StatusKind.Success);

            // Chips: size, and saving (Minify) or line count (Beautify).
            TxtSizeChip.Text = $"{CodeToolkitManager.FormatBytes(result.BeforeBytes)} → {CodeToolkitManager.FormatBytes(result.AfterBytes)}";
            ChipSize.Visibility = Visibility.Visible;

            if (mode == CodeToolMode.Minify)
            {
                bool smaller = result.SavedPercent > 0.05;
                TxtSavedChip.Text = smaller ? $"{result.SavedPercent:0.#}% smaller" : "Already minimal";
                IconSaved.Kind = PackIconKind.ArrowCollapseHorizontal;
            }
            else
            {
                TxtSavedChip.Text = $"{result.BeforeLines:N0} → {result.AfterLines:N0} lines";
                IconSaved.Kind = PackIconKind.FormatIndentIncrease;
            }

            bool firstShow = ChipSaved.Visibility != Visibility.Visible;
            ChipSaved.Visibility = Visibility.Visible;
            ChipAdded.Visibility = ChipRemoved.Visibility = Visibility.Collapsed;
            if (firstShow) UiMotion.Pop(ChipSaved);

            TxtOutputInfo.Text = $"{result.AfterLines:N0} lines · {result.Output.Length:N0} characters";
        }

        private async Task RunDiffAsync(CancellationToken token)
        {
            string original = TxtDiffOriginal.Text, modified = TxtDiffModified.Text;
            if (original.Length == 0 && modified.Length == 0)
            {
                ClearDiff();
                SetStatus("Paste two versions to compare", StatusKind.Neutral);
                return;
            }

            var options = new CodeDiffOptions
            {
                IgnoreWhitespace = ChkIgnoreWhitespace.IsChecked == true,
                IgnoreCase = ChkIgnoreCase.IsChecked == true,
            };

            if (original.Length + modified.Length > 200_000) SetStatus("Comparing…", StatusKind.Busy);

            CodeDiffResult diff;
            try
            {
                diff = await _manager.RunDiffAsync(original, modified, options, token);
            }
            catch (CodeFormatException ex)
            {
                if (token.IsCancellationRequested) return;
                ShowError(ex.Message, null);
                SetStatus(FirstLine(ex.Message), StatusKind.Error);
                return;
            }

            if (token.IsCancellationRequested) return;

            HideError();
            _lastDiff = diff;
            _changeCursor = -1;
            DiffList.ItemsSource = diff.Lines;

            DiffEmptyPanel.Visibility = Visibility.Collapsed;
            if (diff.AreIdentical)
            {
                SetStatus(options.IgnoreWhitespace || options.IgnoreCase
                    ? "No differences (with the ignore options on)"
                    : "No differences - the two versions are identical", StatusKind.Success);
            }
            else
            {
                int blocks = diff.ChangeStarts.Count;
                SetStatus(blocks == 1 ? "1 change found" : $"{blocks:N0} changes found", StatusKind.Warning);
            }

            TxtAddedChip.Text = $"+{diff.AddedCount:N0}";
            TxtRemovedChip.Text = $"−{diff.RemovedCount:N0}";
            bool firstShow = ChipAdded.Visibility != Visibility.Visible;
            ChipAdded.Visibility = ChipRemoved.Visibility = Visibility.Visible;
            ChipSize.Visibility = ChipSaved.Visibility = Visibility.Collapsed;
            if (firstShow)
            {
                UiMotion.Pop(ChipAdded);
                UiMotion.Pop(ChipRemoved);
            }

            BtnPrevChange.IsEnabled = BtnNextChange.IsEnabled = diff.ChangeStarts.Count > 0;
            TxtChangePosition.Text = diff.ChangeStarts.Count switch
            {
                0 => "",
                1 => "1 change",
                int n => $"{n:N0} changes",
            };
        }

        private void ClearOutput()
        {
            TxtOutput.Clear();
            SetOutputOpacity(1);
            _outputIsStale = false;
            OutputEmptyPanel.Visibility = Visibility.Visible;
            BtnCopyOutput.IsEnabled = BtnSaveOutput.IsEnabled = BtnUseOutput.IsEnabled = false;
            TxtOutputInfo.Text = " ";
            HideChips();
        }

        private void MarkOutputStale(string message)
        {
            if (_outputIsStale) return;
            _outputIsStale = true;
            SetOutputOpacity(0.5);
            SetStatus(message, StatusKind.Warning);
        }

        /// <summary>Sets Opacity directly (clearing any FadeIn animation, which would otherwise hold it at 1).</summary>
        private void SetOutputOpacity(double value)
        {
            TxtOutput.BeginAnimation(UIElement.OpacityProperty, null);
            TxtOutput.Opacity = value;
        }

        private void ClearDiff()
        {
            _lastDiff = null;
            _changeCursor = -1;
            DiffList.ItemsSource = null;
            DiffEmptyPanel.Visibility = Visibility.Visible;
            BtnPrevChange.IsEnabled = BtnNextChange.IsEnabled = false;
            TxtChangePosition.Text = "";
            HideChips();
        }

        private void HideChips()
        {
            ChipSize.Visibility = ChipSaved.Visibility = ChipAdded.Visibility = ChipRemoved.Visibility = Visibility.Collapsed;
        }

        // =================================================================
        //  Diff navigation
        // =================================================================

        private void BtnPrevChange_Click(object sender, RoutedEventArgs e) => MoveToChange(-1);
        private void BtnNextChange_Click(object sender, RoutedEventArgs e) => MoveToChange(+1);

        private void MoveToChange(int direction)
        {
            if (_lastDiff is not { ChangeStarts.Count: > 0 } diff) return;

            int count = diff.ChangeStarts.Count;
            _changeCursor = _changeCursor < 0
                ? (direction > 0 ? 0 : count - 1)
                : ((_changeCursor + direction) % count + count) % count;

            var line = diff.Lines[diff.ChangeStarts[_changeCursor]];
            DiffList.SelectedItem = line;
            DiffList.ScrollIntoView(line);
            TxtChangePosition.Text = $"{_changeCursor + 1} of {count}";
        }

        private void BtnSwapDiff_Click(object sender, RoutedEventArgs e)
        {
            (TxtDiffOriginal.Text, TxtDiffModified.Text) = (TxtDiffModified.Text, TxtDiffOriginal.Text);
            if (!IsLive) _ = RunAsync();
        }

        // =================================================================
        //  Files, clipboard
        // =================================================================

        private void BtnLoadFile_Click(object sender, RoutedEventArgs e)
        {
            TextBox target = _mode == CodeToolMode.Diff
                ? (TxtDiffOriginal.Text.Length == 0 ? TxtDiffOriginal : TxtDiffModified)
                : TxtInput;
            PickAndLoad(target);
        }

        private void BtnLoadOriginal_Click(object sender, RoutedEventArgs e) => PickAndLoad(TxtDiffOriginal);
        private void BtnLoadModified_Click(object sender, RoutedEventArgs e) => PickAndLoad(TxtDiffModified);

        private void PickAndLoad(TextBox target)
        {
            var dialog = new OpenFileDialog { Filter = CodeToolkitManager.OpenFileFilter };
            if (dialog.ShowDialog() == true)
                _ = LoadFileIntoAsync(dialog.FileName, target);
        }

        private async Task LoadFileIntoAsync(string path, TextBox target)
        {
            try
            {
                string text = await CodeToolkitManager.ReadFileAsync(path);
                string ext = Path.GetExtension(path);

                if (target == TxtInput)
                {
                    _lastLoadedBaseName = Path.GetFileNameWithoutExtension(path);
                    if (CodeToolkitManager.LanguageFromExtension(ext) is CodeLanguage language)
                    {
                        _languageChosenByUser = false; // the file decides
                        SelectLanguage(language, $"From the {ext.ToLowerInvariant()} file");
                    }
                }

                target.Text = text;
                HideError();
                SetStatus($"Loaded {Path.GetFileName(path)} ({CodeToolkitManager.FormatBytes(new FileInfo(path).Length)})", StatusKind.Success);

                if (!IsLive || text.Length > CodeToolkitManager.LiveUpdateMaxChars)
                    await RunAsync();
            }
            catch (Exception ex)
            {
                ShowError($"Couldn't read {Path.GetFileName(path)}: {ex.Message}", null);
                SetStatus("Couldn't load that file", StatusKind.Error);
            }
        }

        private void BtnPasteInput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    SetStatus("The clipboard has no text", StatusKind.Warning);
                    return;
                }
                TxtInput.Text = Clipboard.GetText();
                TxtInput.Focus();
            }
            catch (Exception ex)
            {
                ShowError($"Couldn't read the clipboard: {ex.Message}", null);
            }
        }

        private void BtnClearInput_Click(object sender, RoutedEventArgs e)
        {
            TxtInput.Clear();
            ClearOutput();
            HideError();
            _languageChosenByUser = false;
            _lastLoadedBaseName = "output";
            LanguageHintPanel.Visibility = Visibility.Collapsed;
            TxtInput.Focus();
        }

        private void BtnUseOutput_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;
            TxtInput.Text = TxtOutput.Text;
            SetStatus("Output moved to Input", StatusKind.Success);
        }

        private void BtnCopyOutput_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;
            try
            {
                Clipboard.SetText(TxtOutput.Text);
                SetStatus("Output copied to the clipboard", StatusKind.Success);
                UiMotion.Pop(BorderStatus);
            }
            catch (Exception ex)
            {
                ShowError($"Couldn't copy: {ex.Message}", null);
            }
        }

        private async void BtnSaveOutput_Click(object sender, RoutedEventArgs e)
        {
            if (TxtOutput.Text.Length == 0) return;

            string ext = CodeToolkitManager.ExtensionFor(_language);
            string suffix = _mode == CodeToolMode.Minify ? ".min" : "";
            var dialog = new SaveFileDialog
            {
                Filter = CodeToolkitManager.FileFilterFor(_language),
                DefaultExt = ext,
                FileName = _lastLoadedBaseName + suffix + ext,
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                await CodeToolkitManager.SaveFileAsync(dialog.FileName, TxtOutput.Text);
                SetStatus($"Saved {Path.GetFileName(dialog.FileName)}", StatusKind.Success);
                UiMotion.Pop(BorderStatus);
            }
            catch (Exception ex)
            {
                ShowError($"Couldn't save: {ex.Message}", null);
            }
        }

        // =================================================================
        //  Drag and drop
        // =================================================================

        private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

        private void Root_PreviewDragEnter(object sender, DragEventArgs e)
        {
            if (!HasFiles(e)) return;

            bool diff = _mode == CodeToolMode.Diff;
            TxtDropTitle.Text = diff ? "Drop a file on a side - or two files to compare" : "Drop a file to load it into Input";
            TxtDropHint.Text = diff ? "The first file becomes Original, the second Modified" : "The language is picked from the file extension";
            UiMotion.FadeVisibility(DropOverlay, true);
        }

        private void Root_PreviewDragOver(object sender, DragEventArgs e)
        {
            // Only take over file drags; dragging selected text inside an editor still works.
            if (!HasFiles(e) || IsOverFilesCard(e.OriginalSource)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        /// <summary>Only hide once the pointer has really left (DragLeave also fires between child elements).</summary>
        private void Root_PreviewDragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
                UiMotion.FadeVisibility(DropOverlay, false);
        }

        private void Root_PreviewDrop(object sender, DragEventArgs e)
        {
            UiMotion.FadeVisibility(DropOverlay, false);
            if (IsOverFilesCard(e.OriginalSource)) return;   // the Files card takes its own drops
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
                return;

            e.Handled = true; // stop the TextBox inserting the file path as text

            var paths = files.Where(File.Exists).ToArray();
            if (paths.Length == 0) return;

            if (_mode == CodeToolMode.Diff)
            {
                if (paths.Length >= 2)
                {
                    _ = LoadFileIntoAsync(paths[0], TxtDiffOriginal);
                    _ = LoadFileIntoAsync(paths[1], TxtDiffModified);
                    return;
                }

                TextBox target = TextBoxUnder(e.OriginalSource as DependencyObject) is TextBox box &&
                                 (box == TxtDiffOriginal || box == TxtDiffModified)
                    ? box
                    : (TxtDiffOriginal.Text.Length == 0 ? TxtDiffOriginal : TxtDiffModified);
                _ = LoadFileIntoAsync(paths[0], target);
                return;
            }

            _ = LoadFileIntoAsync(paths[0], TxtInput);
        }

        private static TextBox? TextBoxUnder(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is TextBox box) return box;
                element = element is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
            }
            return null;
        }

        // =================================================================
        //  Status + errors
        // =================================================================

        private void SetStatus(string text, StatusKind kind)
        {
            TxtStats.Text = text;
            TxtStats.ToolTip = text;

            (string bg, string fg, PackIconKind icon) = kind switch
            {
                StatusKind.Success => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.CheckCircleOutline),
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                StatusKind.Warning => ("StatusWarningBg", "StatusWarningFg", PackIconKind.AlertOutline),
                StatusKind.Busy => ("StatusInfoBg", "StatusInfoFg", PackIconKind.Cached),
                _ => ("RowBg", "TextSecondary", PackIconKind.InformationOutline),
            };

            BorderStatus.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStats.SetResourceReference(TextBlock.ForegroundProperty, fg);
            IconStatus.Kind = icon;
        }

        private void ShowError(string message, CodeFormatException? error)
        {
            _lastError = error;
            TxtError.Text = message;
            TxtErrorSummary.Text = FirstLine(message);

            // Start collapsed; the full text is one click away.
            ExpError.IsExpanded = false;

            bool canJump = error?.Line != null && _mode != CodeToolMode.Diff;
            BtnGoToError.Visibility = canJump ? Visibility.Visible : Visibility.Collapsed;
            if (canJump) TxtGoToError.Text = $"Go to line {error!.Line}";

            if (ErrorBanner.Visibility != Visibility.Visible)
                UiMotion.FadeVisibility(ErrorBanner, true);
        }

        private void HideError()
        {
            _lastError = null;
            ErrorBanner.Visibility = Visibility.Collapsed;
            ExpError.IsExpanded = false;
            TxtError.Text = "";
            TxtErrorSummary.Text = "";
        }

        private void BtnGoToError_Click(object sender, RoutedEventArgs e)
        {
            if (_lastError?.Line is not int line) return;

            int lineIndex = Math.Clamp(line - 1, 0, Math.Max(0, TxtInput.LineCount - 1));
            int start = TxtInput.GetCharacterIndexFromLineIndex(lineIndex);
            if (start < 0) return;

            int column = Math.Max(0, (_lastError.Column ?? 1) - 1);
            int lineLength = TxtInput.GetLineLength(lineIndex);
            int caret = start + Math.Min(column, Math.Max(0, lineLength));

            TxtInput.Focus();
            TxtInput.Select(caret, 0);
            TxtInput.ScrollToLine(lineIndex);
        }

        private static string FirstLine(string message)
        {
            int newline = message.IndexOfAny(new[] { '\r', '\n' });
            return newline >= 0 ? message[..newline] : message;
        }
    }
}
