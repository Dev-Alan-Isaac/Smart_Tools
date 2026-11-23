using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;


namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Regex Tester - UI only. Matching, line:column, the replace preview,
    /// reading files, folder search, CSV and settings are in
    /// <see cref="RegexManager"/>; the plain-words breakdown is in
    /// RegexManager.Breakdown.cs. Live matching runs off the UI thread,
    /// ~200 ms after the last key, and a newer key cancels the older run.
    /// The file and folder picked here are kept for this session only.
    /// </summary>
    public partial class RegexTesterView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_regex-tester_Name", "Regex Tester");

        /// <summary>The highlighted copy shows this much text; every match is still counted and listed.</summary>
        private const int MaxPreviewChars = 300_000;

        /// <summary>Quick reference: tokens (each one a clickable chip) and the string key that explains them.</summary>
        private static readonly (string[] Tokens, string? Key, string? Text)[] QuickReference =
        {
            (new[] { @"\d", @"\w", @"\s" }, "Str_Regex_QrDigitWordSpace", null),
            (new[] { @"\D", @"\W", @"\S" }, "Str_Regex_QrOpposite", null),
            (new[] { "." }, "Str_Regex_QrAnyChar", null),
            (new[] { "[abc]" }, "Str_Regex_QrAnyOfSet", null),
            (new[] { "[a-z]" }, "Str_Regex_QrRange", null),
            (new[] { "[^abc]" }, "Str_Regex_QrExcept", null),
            (new[] { "?" }, "Str_Regex_QrZeroOrOne", null),
            (new[] { "*" }, "Str_Regex_QrZeroOrMore", null),
            (new[] { "+" }, "Str_Regex_QrOneOrMore", null),
            (new[] { "{2,4}" }, "Str_Regex_QrBetween", null),
            (new[] { "*?", "+?" }, "Str_Regex_QrLazy", null),
            (new[] { "(abc)" }, "Str_Regex_QrGroup", null),
            (new[] { "(?<name>abc)" }, "Str_Regex_QrNamedGroup", null),
            (new[] { "(?:abc)" }, "Str_Regex_QrNonCapturing", null),
            (new[] { @"\1" }, "Str_Regex_QrBackref", null),
            (new[] { "a|b" }, "Str_Regex_QrEither", null),
            (new[] { "^", "$" }, "Str_Regex_QrStartEnd", null),
            (new[] { @"\b" }, "Str_Regex_QrWordEdge", null),
            (new[] { "(?=abc)", "(?!abc)" }, "Str_Regex_QrLookaround", null),
            (new[] { @"\." }, "Str_Regex_QrLiteral", null),
        };

        private readonly DispatcherTimer _liveTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly bool _viewConstructed;

        private RegexInputMode _mode = RegexInputMode.Text;
        private CancellationTokenSource? _liveCts;
        private CancellationTokenSource? _runCts;
        private RegexTextResult? _lastText;
        private RegexFolderResult? _lastFolder;
        private string? _loadedFilePath;    // session only, never saved
        private string? _folderPath;        // session only, never saved
        private bool _isRunning;
        private bool _isLoadingFile;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private bool _settingInputText;
        private string _lastPillState = "";

        public RegexTesterView()
        {
            InitializeComponent();

            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _liveTimer.Tick += (_, _) =>
            {
                _liveTimer.Stop();
                _ = RunLiveAsync();
            };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettings();
            };

            PopulatePatternPresets();
            BuildQuickReference();
            _viewConstructed = true;

            // F5 by default; the key can be changed in Settings.
            ShortcutManager.AttachCommon("regex-tester", ShortcutManager.RunId, () => BtnRunTest_Click(this, new RoutedEventArgs()));
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a folder search and live matching, and saves the options.</summary>
        public void CancelActiveRun()
        {
            try
            {
                _liveTimer.Stop();
                _liveCts?.Cancel();
                _runCts?.Cancel();
                _lifetimeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SaveSettings();
            }
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        // =================================================================
        //  Layout
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ApplySettings(RegexManager.LoadSettings());
            UpdateBreakdown();
            ScheduleLive(immediately: true);
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Narrow window: let the sidebar get out of the way.
            if (e.NewSize.Width < 860 && !_optionsCollapsed && e.PreviousSize.Width >= 860) BtnCollapseOptions_Click(this, new RoutedEventArgs());
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Run is a shortcut action (see the constructor).
            if (e.Key == Key.Escape && _isRunning)
            {
                _runCts?.Cancel();
                e.Handled = true;
            }
        }

        // =================================================================
        //  Settings (pattern and flags only - no test text, no paths)
        // =================================================================

        private void ApplySettings(RegexSettings s)
        {
            _applyingSettings = true;
            try
            {
                PatternTextBox.Text = s.Pattern;
                TxtReplacement.Text = s.Replacement;
                ChkIgnoreCase.IsChecked = s.IgnoreCase;
                ChkMultiline.IsChecked = s.Multiline;
                ChkSingleline.IsChecked = s.Singleline;
                ChkIgnoreWhitespace.IsChecked = s.IgnoreWhitespace;
                ChkIncludeSubfolders.IsChecked = s.IncludeSubfolders;
                TxtFileFilter.Text = s.FileFilter;
                (s.Mode switch
                {
                    RegexInputMode.File => RadioModeFile,
                    RegexInputMode.Folder => RadioModeFolder,
                    _ => RadioModeText,
                }).IsChecked = true;
            }
            finally
            {
                _applyingSettings = false;
            }
            SetMode(s.Mode);
            UpdateFlagLetters();
        }

        private void SaveSettings() => RegexManager.SaveSettings(new RegexSettings
        {
            Pattern = PatternTextBox.Text,
            Replacement = TxtReplacement.Text,
            IgnoreCase = ChkIgnoreCase.IsChecked == true,
            Multiline = ChkMultiline.IsChecked == true,
            Singleline = ChkSingleline.IsChecked == true,
            IgnoreWhitespace = ChkIgnoreWhitespace.IsChecked == true,
            Mode = _mode,
            IncludeSubfolders = ChkIncludeSubfolders.IsChecked == true,
            FileFilter = TxtFileFilter.Text,
        });

        private void QueueSave()
        {
            if (Ignore) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private RegexTestOptions ReadOptions() => new()
        {
            Pattern = PatternTextBox.Text,
            IgnoreCase = ChkIgnoreCase.IsChecked == true,
            Multiline = ChkMultiline.IsChecked == true,
            Singleline = ChkSingleline.IsChecked == true,
            IgnoreWhitespace = ChkIgnoreWhitespace.IsChecked == true,
            Replacement = TxtReplacement.Text,
        };

        // =================================================================
        //  Examples and quick reference
        // =================================================================

        private void PopulatePatternPresets()
        {
            CmbPatternPresets.Items.Add(new ComboBoxItem { Content = "Choose an example..." });
            foreach (var preset in RegexManager.Presets)
                CmbPatternPresets.Items.Add(new ComboBoxItem { Content = preset.Name, ToolTip = preset.Pattern });
            CmbPatternPresets.SelectedIndex = 0;
        }

        private void CmbPatternPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            int index = CmbPatternPresets.SelectedIndex - 1;   // 0 is "Choose an example..."
            if (index < 0 || index >= RegexManager.Presets.Count) return;

            var preset = RegexManager.Presets[index];
            if (preset.Pattern.StartsWith('^') && preset.Sample.Contains('\n')) ChkMultiline.IsChecked = true;
            PatternTextBox.Text = preset.Pattern;
            // Only replace the test text in Text mode - in File mode it's the file's content.
            if (_mode == RegexInputMode.Text) TxtInput.Text = preset.Sample;
            UpdateFlagLetters();

            // Back to the prompt so the same example can be picked again.
            _ = Dispatcher.InvokeAsync(() => CmbPatternPresets.SelectedIndex = 0, DispatcherPriority.Background);
        }

        private void BuildQuickReference()
        {
            foreach (var (tokens, key, text) in QuickReference)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Top };
                foreach (string token in tokens)
                {
                    var chipText = new TextBlock { Text = token, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.SemiBold };
                    chipText.SetResourceReference(TextBlock.ForegroundProperty, "AccentColor");
                    var chip = new Border
                    {
                        Child = chipText,
                        Padding = new Thickness(6, 1, 6, 1),
                        Margin = new Thickness(0, 0, 4, 2),
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(1),
                        Cursor = Cursors.Hand,
                        ToolTip = string.Format(TryFindResource("Str_Regex_QrInsertTooltip") as string ?? "Insert {0}", token),
                        Tag = token,
                    };
                    chip.SetResourceReference(Border.BackgroundProperty, "SidebarInputBg");
                    chip.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");
                    chip.MouseEnter += (_, _) => chip.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
                    chip.MouseLeave += (_, _) => chip.SetResourceReference(Border.BorderBrushProperty, "SidebarInputBorder");
                    chip.MouseLeftButtonUp += QuickRefChip_Click;
                    chips.Children.Add(chip);
                }
                row.Children.Add(chips);

                var meaning = new TextBlock { FontSize = 10.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                meaning.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextMuted");
                if (key != null) meaning.SetResourceReference(TextBlock.TextProperty, key);
                else meaning.Text = text;
                Grid.SetColumn(meaning, 1);
                row.Children.Add(meaning);

                QuickRefList.Children.Add(row);
            }
        }

        /// <summary>Puts the token into the pattern at the cursor (replacing a selection).</summary>
        private void QuickRefChip_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: string token } chip) return;
            int start = PatternTextBox.SelectionStart;
            PatternTextBox.SelectedText = token;
            PatternTextBox.SelectionLength = 0;
            PatternTextBox.CaretIndex = start + token.Length;
            PatternTextBox.Focus();
            UiMotion.Pop(chip, 0.85);
        }

        // =================================================================
        //  Pattern, flags, replacement
        // =================================================================

        private void PatternTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateBreakdown();
            ScheduleLive();
            QueueSave();
        }

        private void TxtReplacement_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            ScheduleLive();
            QueueSave();
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateFlagLetters();
            UpdateBreakdown();   // anchors, the dot and spaces read differently with the flags
            ScheduleLive(immediately: true);
            QueueSave();
        }

        private void TxtFileFilter_TextChanged(object sender, TextChangedEventArgs e) => QueueSave();

        private void UpdateFlagLetters()
        {
            string letters = (ChkIgnoreCase.IsChecked == true ? "i" : "")
                           + (ChkMultiline.IsChecked == true ? "m" : "")
                           + (ChkSingleline.IsChecked == true ? "s" : "")
                           + (ChkIgnoreWhitespace.IsChecked == true ? "x" : "");
            TxtFlagLetters.Text = "/" + letters;
            TxtFlagsSummary.Text = letters.Length == 0 ? "No flags on" : $"Inline: (?{letters})";
        }

        private void BtnCopyPattern_Click(object sender, RoutedEventArgs e)
        {
            if (PatternTextBox.Text.Length == 0) return;
            if (TrySetClipboard(PatternTextBox.Text)) UiMotion.Pop(BtnCopyPattern, 0.85);
        }

        private void BtnCopyReplaced_Click(object sender, RoutedEventArgs e)
        {
            if (_lastText?.Replaced is string replaced && TrySetClipboard(replaced)) UiMotion.Pop(BtnCopyReplaced, 0.85);
        }

        private static bool TrySetClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;   // another program has the clipboard open
            }
        }

        // ---------------- Breakdown ----------------

        private void UpdateBreakdown()
        {
            var rows = RegexManager.BuildBreakdown(PatternTextBox.Text, ReadOptions());
            DataGridBreakdown.ItemsSource = rows;
            bool any = rows.Count > 0;
            DataGridBreakdown.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if ((BreakdownEmptyStatePanel.Visibility == Visibility.Visible) == any) UiMotion.FadeVisibility(BreakdownEmptyStatePanel, !any);
        }

        /// <summary>Selecting a piece of the breakdown selects it in the pattern box.</summary>
        private void DataGridBreakdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataGridBreakdown.SelectedItem is not RegexBreakdownRow { Start: >= 0 } row || PatternTextBox.IsKeyboardFocusWithin) return;
            if (row.Start + row.Token.Length <= PatternTextBox.Text.Length) PatternTextBox.Select(row.Start, row.Token.Length);
        }

        // =================================================================
        //  Source: text, file, folder
        // =================================================================

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore || sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out RegexInputMode mode)) return;
            if (_isRunning)
            {
                // Keep the chips on the running mode; the search uses what was set when it started.
                ReselectModeChip();
                return;
            }
            SetMode(mode);
            QueueSave();
        }

        private void ReselectModeChip() =>
            (_mode switch
            {
                RegexInputMode.File => RadioModeFile,
                RegexInputMode.Folder => RadioModeFolder,
                _ => RadioModeText,
            }).IsChecked = true;

        private void SetMode(RegexInputMode mode)
        {
            _mode = mode;
            bool folder = mode == RegexInputMode.Folder;
            PanelFileSource.Visibility = mode == RegexInputMode.File ? Visibility.Visible : Visibility.Collapsed;
            PanelFolderSource.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;

            if (folder != (FolderPanel.Visibility == Visibility.Visible))
            {
                UiMotion.FadeVisibility(FolderPanel, folder);
                UiMotion.FadeVisibility(TextFilePanel, !folder);
            }
            ProgressRegex.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
            TxtProgressPercent.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;

            TxtModeHint.Text = mode switch
            {
                RegexInputMode.File => "Open a text file (up to 20 MB) and test on its content. Edits here never change the file.",
                RegexInputMode.Folder => "Search every matching file in a folder. Click Search (or press F5) to start.",
                _ => "Type or paste text below - matches update as you type.",
            };
            UpdateRunButton();
            UpdateFileInfo();
            UpdateExportState();

            if (folder) ShowFolderResult(_lastFolder);
            ScheduleLive(immediately: true);   // Folder mode: just checks the pattern
        }

        private void UpdateRunButton()
        {
            if (_isRunning)
            {
                IconRunTest.Kind = PackIconKind.Stop;
                TxtRunTestLabel.Text = "Cancel";
                return;
            }
            (IconRunTest.Kind, TxtRunTestLabel.Text) = _mode switch
            {
                RegexInputMode.Folder => (PackIconKind.FolderSearchOutline, "Search folder"),
                RegexInputMode.File => (PackIconKind.Reload, _loadedFilePath != null ? "Reload file" : "Choose file"),
                _ => (PackIconKind.Play, "Test now"),
            };
        }

        private void UpdateFileInfo()
        {
            string text = TxtInput.Text;
            int length = text.Length;
            int lines = length == 0 ? 0 : text.AsSpan().Count('\n') + 1;
            string size = $"{length:N0} {RegexText.Plural(length, "character", "characters")}" + (lines > 0 ? $" · {lines:N0} {RegexText.Plural(lines, "line", "lines")}" : "");
            TxtInputInfo.Text = _mode == RegexInputMode.File && _loadedFilePath != null ? $"{Path.GetFileName(_loadedFilePath)} · {size}" : size;
        }

        private async void BtnChooseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose a text file to test",
                Filter = "Text files (*.txt;*.log;*.csv;*.json;*.xml;*.md;*.ini;*.cfg)|*.txt;*.log;*.csv;*.json;*.xml;*.md;*.ini;*.cfg|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true) return;
            await LoadFileAsync(dialog.FileName);
        }

        private async Task LoadFileAsync(string path)
        {
            if (_isLoadingFile) return;
            _isLoadingFile = true;
            BtnChooseFile.IsEnabled = false;
            TxtFileInfo.Text = "Reading…";
            try
            {
                var (text, error) = await RegexManager.LoadFileAsync(path);
                if (_lifetimeCts.IsCancellationRequested) return;
                if (text == null)
                {
                    TxtFileInfo.Text = error ?? "It couldn't be read.";
                    UiMotion.Pop(TxtFileInfo, 0.9);
                    return;
                }
                _loadedFilePath = path;
                TxtFilePath.Text = Path.GetFileName(path);
                TxtFilePath.ToolTip = path;
                TxtFileInfo.Text = RegexText.Bytes(new FileInfo(path).Length) + " · " + (Path.GetDirectoryName(path) ?? "");
                TxtFileInfo.ToolTip = path;
                _settingInputText = true;
                try { TxtInput.Text = text; }
                finally { _settingInputText = false; }
                UiMotion.FadeIn(TxtInput, 6);
                UpdateRunButton();
                UpdateFileInfo();
                ScheduleLive(immediately: true);
            }
            catch (IOException ex)
            {
                TxtFileInfo.Text = ex.Message;
            }
            finally
            {
                _isLoadingFile = false;
                BtnChooseFile.IsEnabled = true;
            }
        }

        private void BtnChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            var dialog = new OpenFolderDialog { Title = "Choose a folder to search" };
            if (dialog.ShowDialog() != true) return;
            SetFolder(dialog.FolderName);
        }

        private void SetFolder(string folder)
        {
            _folderPath = folder;
            TxtFolderPath.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : folder;
            TxtFolderPath.ToolTip = folder;
            if (_mode != RegexInputMode.Folder)
            {
                RadioModeFolder.IsChecked = true;
                SetMode(RegexInputMode.Folder);
            }
            UiMotion.Pop(TxtFolderPath, 0.9);
            TxtFolderEmptyTitle.Text = "Ready to search " + TxtFolderPath.Text;
            TxtFolderEmptyHint.Text = "Click Search folder (or press F5). Only files matching the filter in the sidebar are read.";
        }

        // ---------------- Drag and drop ----------------

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isRunning && e.Data.GetDataPresent(DataFormats.FileDrop);
            if (!ok) return;   // plain text dragged into a text box behaves as usual
            e.Effects = DragDropEffects.Copy;
            if (DropOverlay.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropOverlay, true);
            e.Handled = true;
        }

        /// <summary>DragLeave also fires when moving between child elements - only hide once the pointer is really outside.</summary>
        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
        }

        private async void Root_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (_isRunning || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
            e.Handled = true;
            string path = paths[0];
            if (Directory.Exists(path))
            {
                SetFolder(path);
            }
            else if (File.Exists(path))
            {
                if (_mode != RegexInputMode.File)
                {
                    RadioModeFile.IsChecked = true;
                    SetMode(RegexInputMode.File);
                }
                await LoadFileAsync(path);
            }
        }

        // =================================================================
        //  Live matching (Text / File)
        // =================================================================

        private void TxtInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateFileInfo();
            if (!_settingInputText) ScheduleLive();
        }

        private void ScheduleLive(bool immediately = false)
        {
            if (!_viewConstructed) return;
            if (_mode == RegexInputMode.Folder)
            {
                CheckPatternForFolder();
                return;
            }
            _liveTimer.Stop();
            if (immediately) _ = RunLiveAsync();
            else _liveTimer.Start();
        }

        /// <summary>Folder mode has no live text, but the pattern is still checked as it's typed.</summary>
        private void CheckPatternForFolder()
        {
            if (_isRunning) return;
            var options = ReadOptions();
            if (options.Pattern.Length == 0)
            {
                ShowPatternError(null, -1);
                SetPill("empty", "Type a pattern", PackIconKind.Regex, "RowBg", "TextSecondary");
                return;
            }
            bool ok = RegexManager.TryCreate(options, RegexManager.FileTimeout, out string? error, out int offset) != null;
            ShowPatternError(ok ? null : error, offset);
            if (ok) SetPill("ready", "Ready to search", PackIconKind.CheckCircleOutline, "StatusInfoBg", "StatusInfoFg");
            else SetPill("error", "Invalid pattern", PackIconKind.AlertCircleOutline, "StatusErrorBg", "StatusErrorFg");
        }

        private async Task RunLiveAsync()
        {
            if (_mode == RegexInputMode.Folder) return;
            _liveCts?.Cancel();
            if (_lifetimeCts.IsCancellationRequested) return;
            var cts = new CancellationTokenSource();   // not linked: a linked source per key press would pile up registrations
            _liveCts = cts;

            var options = ReadOptions();
            string text = TxtInput.Text;
            if (options.Pattern.Length == 0)
            {
                _lastText = null;
                ShowPatternError(null, -1);
                SetPill("empty", "Type a pattern", PackIconKind.Regex, "RowBg", "TextSecondary");
                RenderText(text, Array.Empty<RegexSpan>());
                ShowRows(Array.Empty<Models.RegexMatchRow>(), "Matches and their groups show up here as you type.");
                PanelOutputView.Visibility = Visibility.Collapsed;
                TxtProgress.Text = "Type a pattern to start.";
                UpdateExportState();
                return;
            }

            string fileName = _mode == RegexInputMode.File && _loadedFilePath != null ? Path.GetFileName(_loadedFilePath) : "(text)";
            string fullPath = _mode == RegexInputMode.File ? _loadedFilePath ?? "" : "";
            // Say "Matching…" only when it takes a while (no flicker on quick runs).
            _ = Task.Delay(150, cts.Token).ContinueWith(_ =>
            {
                if (!cts.IsCancellationRequested) TxtProgress.Text = "Matching…";
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());

            RegexTextResult result;
            try
            {
                result = await RegexManager.MatchTextAsync(text, options, cts.Token, fileName, fullPath);
            }
            catch (OperationCanceledException)
            {
                return;   // a newer key press took over
            }
            finally
            {
                if (_liveCts == cts) cts.Cancel();   // stops the "Matching…" hint
            }
            if (_liveCts != cts) return;
            _lastText = result;
            ShowTextResult(text, result);
        }

        private void ShowTextResult(string text, RegexTextResult r)
        {
            ShowPatternError(r.Error, r.ErrorOffset);
            TxtProgress.Text = RegexManager.DescribeResult(r);

            if (r.Error != null)
                SetPill("error", "Invalid pattern", PackIconKind.AlertCircleOutline, "StatusErrorBg", "StatusErrorFg");
            else if (r.TimedOut)
                SetPill("slow", "Too slow", PackIconKind.TimerAlertOutline, "StatusErrorBg", "StatusErrorFg");
            else if (r.MatchCount == 0)
                SetPill("none", "No match", PackIconKind.CloseCircleOutline, "StatusWarningBg", "StatusWarningFg");
            else
                SetPill("ok:" + r.MatchCount, r.MatchCount >= RegexManager.MaxMatches ? $"{RegexManager.MaxMatches:N0}+ matches" : $"{r.MatchCount:N0} {RegexText.Plural(r.MatchCount, "match", "matches")}",
                    PackIconKind.CheckCircleOutline, "StatusSuccessBg", "StatusSuccessFg");

            bool hasReplace = r.Replaced != null;
            PanelOutputView.Visibility = hasReplace ? Visibility.Visible : Visibility.Collapsed;
            if (!hasReplace) RadioViewMatches.IsChecked = true;

            if (hasReplace && RadioViewReplaced.IsChecked == true) RenderPlain(r.Replaced!);
            else RenderText(text, r.Error == null ? r.Highlights : Array.Empty<RegexSpan>());

            ShowRows(r.Rows, r.Error != null ? "Fix the pattern to see matches." : r.TimedOut ? "The pattern was stopped before it found anything." : "No matches in this text.");
            UpdateExportState();
        }

        private void OutputView_Click(object sender, RoutedEventArgs e)
        {
            if (_lastText == null) return;
            if (RadioViewReplaced.IsChecked == true && _lastText.Replaced != null) RenderPlain(_lastText.Replaced);
            else RenderText(TxtInput.Text, _lastText.Error == null ? _lastText.Highlights : Array.Empty<RegexSpan>());
            UiMotion.FadeIn(RichTextOutput, 4);
        }

        private void SetPill(string state, string text, PackIconKind icon, string bgKey, string fgKey)
        {
            TxtMatchState.Text = text;
            IconMatchState.Kind = icon;
            PillMatchState.SetResourceReference(Border.BackgroundProperty, bgKey);
            TxtMatchState.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            if (state != _lastPillState) UiMotion.Pop(PillMatchState, 0.9);
            _lastPillState = state;
        }

        private void ShowPatternError(string? error, int offset)
        {
            bool show = error != null;
            if (show)
            {
                TxtPatternError.Text = error;
                string pattern = PatternTextBox.Text;
                // Point at the spot: the pattern with a ^ under it (short, one-line patterns only).
                if (offset >= 0 && pattern.Length <= 90 && !pattern.Contains('\n'))
                {
                    int at = Math.Clamp(offset - 1, 0, Math.Max(0, pattern.Length - 1));
                    TxtPatternErrorPointer.Text = pattern + "\n" + new string(' ', at) + "^";
                    TxtPatternErrorPointer.Visibility = Visibility.Visible;
                }
                else TxtPatternErrorPointer.Visibility = Visibility.Collapsed;
            }
            if (show != (PatternErrorBanner.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(PatternErrorBanner, show);
        }

        private void ShowRows(IReadOnlyList<Models.RegexMatchRow> rows, string emptyText)
        {
            DataGridGroupsSingle.ItemsSource = rows;
            bool any = rows.Count > 0;
            DataGridGroupsSingle.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            TxtGroupsEmpty.Text = emptyText;
            GroupsEmptyStatePanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// The text with each match highlighted. Neighbouring matches alternate
        /// two tones; an empty match (\b, x*) is a thin caret - the old view
        /// inserted a space, so the highlighted copy wasn't the real text.
        /// </summary>
        private void RenderText(string text, IReadOnlyList<RegexSpan> spans)
        {
            bool cut = text.Length > MaxPreviewChars;
            string shown = cut ? text[..MaxPreviewChars] : text;
            var paragraph = new Paragraph();
            int last = 0;
            for (int i = 0; i < spans.Count; i++)
            {
                var s = spans[i];
                if (s.Start > shown.Length) break;
                if (s.Start > last) paragraph.Inlines.Add(new Run(shown[last..s.Start]));
                if (s.Length == 0)
                {
                    var caret = new Border { Width = 2, Height = 14, Margin = new Thickness(0, 0, 0, -2), ToolTip = $"Empty match #{i + 1} at {s.Start:N0}" };
                    caret.SetResourceReference(Border.BackgroundProperty, "AccentColor");
                    paragraph.Inlines.Add(new InlineUIContainer(caret) { BaselineAlignment = BaselineAlignment.Center });
                    last = Math.Max(last, s.Start);
                    continue;
                }
                int end = Math.Min(s.Start + s.Length, shown.Length);
                var run = new Run(shown[s.Start..end]) { ToolTip = $"Match #{i + 1} · position {s.Start:N0} · {s.Length:N0} {RegexText.Plural(s.Length, "character", "characters")}" };
                run.SetResourceReference(TextElement.BackgroundProperty, i % 2 == 0 ? "AccentColorSubtle" : "StatusWarningBg");
                paragraph.Inlines.Add(run);
                last = end;
            }
            if (last < shown.Length) paragraph.Inlines.Add(new Run(shown[last..]));
            if (cut)
            {
                var note = new Run($"\n\n… the highlighted copy shows the first {MaxPreviewChars:N0} characters; every match is still counted and listed.") { FontStyle = FontStyles.Italic };
                note.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");
                paragraph.Inlines.Add(note);
            }
            RichTextOutput.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
        }

        private void RenderPlain(string text)
        {
            string shown = text.Length > MaxPreviewChars ? text[..MaxPreviewChars] + "\n\n…" : text;
            RichTextOutput.Document = new FlowDocument(new Paragraph(new Run(shown))) { PagePadding = new Thickness(0) };
        }

        /// <summary>Clicking a group row selects that text in the test text box.</summary>
        private void DataGridGroupsSingle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataGridGroupsSingle.SelectedItem is not Models.RegexMatchRow row) return;
            if (row.Position + row.Length > TxtInput.Text.Length) return;   // text changed since
            TxtInput.Select(row.Position, row.Length);
            int line = TxtInput.GetLineIndexFromCharacterIndex(row.Position);
            if (line >= 0) TxtInput.ScrollToLine(line);
        }

        // =================================================================
        //  Run button: test now / reload file / search folder
        // =================================================================

        private async void BtnRunTest_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                return;
            }
            switch (_mode)
            {
                case RegexInputMode.Text:
                    ScheduleLive(immediately: true);
                    break;
                case RegexInputMode.File:
                    if (_loadedFilePath != null && File.Exists(_loadedFilePath)) await LoadFileAsync(_loadedFilePath);   // picks up changes on disk
                    else BtnChooseFile_Click(sender, e);
                    break;
                case RegexInputMode.Folder:
                    await RunFolderAsync();
                    break;
            }
        }

        private async Task RunFolderAsync()
        {
            if (_folderPath == null || !Directory.Exists(_folderPath))
            {
                TxtProgress.Text = _folderPath == null ? "Choose a folder first." : "That folder isn't there any more - choose it again.";
                UiMotion.Pop(BtnChooseFolder, 0.9);
                return;
            }
            var options = ReadOptions();
            if (RegexManager.TryCreate(options, RegexManager.FileTimeout, out string? error, out int offset) == null)
            {
                ShowPatternError(error, offset);
                SetPill("error", "Invalid pattern", PackIconKind.AlertCircleOutline, "StatusErrorBg", "StatusErrorFg");
                TxtProgress.Text = "Fix the pattern first: " + error;
                return;
            }
            ShowPatternError(null, -1);

            _isRunning = true;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            SetRunningUi(true);
            ProgressRegex.Value = 0;
            TxtProgressPercent.Text = "0%";
            TxtProgress.Text = "Finding files…";
            var clock = Stopwatch.StartNew();

            try
            {
                var progress = new Progress<RegexProgress>(p =>
                {
                    if (!_isRunning) return;
                    ProgressRegex.Value = p.Percent;
                    TxtProgressPercent.Text = $"{p.Percent:0}%";
                    TxtProgress.Text = $"{p.Done:N0} of {p.Total:N0} files · {p.Matches:N0} {RegexText.Plural(p.Matches, "match", "matches")}" + (p.CurrentFile.Length > 0 ? " · " + p.CurrentFile : "");
                });
                var result = await RegexManager.RunOnFolderAsync(_folderPath, options, ChkIncludeSubfolders.IsChecked == true, TxtFileFilter.Text, progress, _runCts.Token);
                if (_lifetimeCts.IsCancellationRequested) return;
                _lastFolder = result;
                ShowFolderResult(result);
                ProgressRegex.Value = result.Cancelled ? ProgressRegex.Value : 100;
                TxtProgressPercent.Text = $"{ProgressRegex.Value:0}%";
                TxtProgress.Text = RegexManager.DescribeFolder(result);
                if (result.Error == null)
                    SetPill("folder:" + result.MatchCount, $"{result.MatchCount:N0} {RegexText.Plural(result.MatchCount, "match", "matches")}",
                        result.MatchCount > 0 ? PackIconKind.CheckCircleOutline : PackIconKind.CloseCircleOutline,
                        result.MatchCount > 0 ? "StatusSuccessBg" : "StatusWarningBg", result.MatchCount > 0 ? "StatusSuccessFg" : "StatusWarningFg");
                if (clock.Elapsed > TimeSpan.FromSeconds(8) && !result.Cancelled) Toast("Regex search finished", RegexManager.DescribeFolder(result));
            }
            catch (OperationCanceledException)
            {
                TxtProgress.Text = "Stopped.";
            }
            finally
            {
                _isRunning = false;
                _runCts?.Dispose();
                _runCts = null;
                SetRunningUi(false);
            }
        }

        private void SetRunningUi(bool running)
        {
            UpdateRunButton();
            BtnChooseFolder.IsEnabled = !running;
            BtnEmptyChooseFolder.IsEnabled = !running;
            ChkIncludeSubfolders.IsEnabled = !running;
            TxtFileFilter.IsEnabled = !running;
            UpdateExportState();
        }

        private void ShowFolderResult(RegexFolderResult? r)
        {
            bool any = r != null && r.Rows.Count > 0;
            DataGridGroupsFolder.ItemsSource = r?.Rows ?? Array.Empty<Models.RegexMatchRow>();
            DataGridGroupsFolder.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (any) UiMotion.FadeIn(DataGridGroupsFolder, 6);

            FolderEmptyStatePanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            if (r != null && !any)
            {
                TxtFolderEmptyTitle.Text = r.Error != null ? "The search couldn't run" : r.Cancelled ? "Stopped before anything was found" : "No matches in this folder";
                TxtFolderEmptyHint.Text = r.Error ?? (r.FilesScanned == 0
                    ? "No files were searched - check the file filter in the sidebar (* searches every file)."
                    : $"{r.FilesScanned:N0} {RegexText.Plural(r.FilesScanned, "file was", "files were")} searched. Try the pattern on one of them in File mode, or turn on Ignore case.");
                UiMotion.Pop(FolderEmptyBadge, 0.9);
            }

            bool pills = r != null && r.Error == null;
            FolderPillsPanel.Visibility = pills ? Visibility.Visible : Visibility.Collapsed;
            if (!pills) return;

            TxtSummaryCount.Text = $"{r!.MatchCount:N0} {RegexText.Plural(r.MatchCount, "match", "matches")}";
            TxtFilesCount.Text = $"{r.FilesWithMatches:N0} of {r.FilesScanned:N0} {RegexText.Plural(r.FilesScanned, "file", "files")}";
            PillSkipped.Visibility = r.Skipped.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSkippedCount.Text = $"{r.Skipped.Count:N0} skipped";
            PillSkipped.ToolTip = r.Skipped.Count == 0 ? null : string.Join("\n", r.Skipped.Take(25).Select(s => $"{s.FileName}: {s.Reason}"))
                + (r.Skipped.Count > 25 ? $"\n… and {r.Skipped.Count - 25:N0} more" : "");
            UiMotion.Pop(SummaryPill, 0.9);
        }

        // =================================================================
        //  Export
        // =================================================================

        private IReadOnlyList<Models.RegexMatchRow> CurrentRows() =>
            _mode == RegexInputMode.Folder ? _lastFolder?.Rows ?? Array.Empty<Models.RegexMatchRow>() : _lastText?.Rows ?? Array.Empty<Models.RegexMatchRow>();

        private void UpdateExportState() => BtnExportCsv.IsEnabled = !_isRunning && CurrentRows().Count > 0;

        private async void MenuExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var rows = CurrentRows();
            if (rows.Count == 0) return;
            bool truncated = _mode == RegexInputMode.Folder ? _lastFolder?.Truncated == true : _lastText?.Truncated == true;

            var dialog = new SaveFileDialog
            {
                Title = "Export matches to CSV",
                Filter = "CSV file|*.csv",
                DefaultExt = ".csv",
                FileName = "regex-matches.csv",
            };
            if (dialog.ShowDialog() != true) return;

            BtnExportCsv.IsEnabled = false;
            try
            {
                await RegexManager.ExportCsvAsync(rows, dialog.FileName);
                TxtProgress.Text = $"Saved {rows.Count:N0} {RegexText.Plural(rows.Count, "row", "rows")} to {Path.GetFileName(dialog.FileName)}" + (truncated ? " (the first rows only - the list is capped)." : ".");
                UiMotion.Pop(BtnExportCsv, 0.9);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppModal.Show(Loc.T("Str_MB_CouldnTSaveThe", "Couldn't save the CSV: ") + RegexManager.PlainSaveError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateExportState();
            }
        }

        private static void Toast(string title, string message)
        {
            try
            {
                ToastNotificationManager.ShowToast(title, message);
            }
            catch (Exception)
            {
                // a toast is a nice-to-have
            }
        }
    }
}