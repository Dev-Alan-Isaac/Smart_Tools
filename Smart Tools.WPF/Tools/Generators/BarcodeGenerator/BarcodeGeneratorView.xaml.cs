using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using Path = System.IO.Path;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Barcode Generator view - UI only. All encoding, validation, batch
    /// work and export live in <see cref="BarcodeManager"/>; data shapes in
    /// BarcodeModel.cs.
    ///
    /// Single mode regenerates live on every option change. Batch mode runs
    /// on the manager's STA worker with progress + cancel, keeps thumbnails
    /// only, and re-renders at full size on export.
    /// </summary>
    public partial class BarCodeGeneratorView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DefaultForeground = "#000000";
        private const string DefaultBackground = "#FFFFFF";
        private static string DialogTitle => Loc.T("Str_Tool_barcode-generator_Name", "Barcode Generator");

        private static readonly Regex HexPattern = new("^#?[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

        private readonly BarcodeManager _barcode = new();
        private readonly ObservableCollection<string> _batchEntries = new();

        private BarcodeGenerationResult? _currentResult;
        private List<BatchBarcodeRow>? _batchRows;
        private CancellationTokenSource? _batchCts;
        private bool _isBatchGenerating;
        private bool _isExporting;
        private bool _optionsCollapsed;
        private readonly bool _viewConstructed;

        private enum StatusKind { Neutral, Success, Error }

        public BarCodeGeneratorView()
        {
            InitializeComponent();
            ListBoxBatchEntries.ItemsSource = _batchEntries;
            _batchEntries.CollectionChanged += BatchEntries_CollectionChanged;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun() => _batchCts?.Cancel();

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateTypeHint();
            UpdateSwatches();
            UpdateColorWarning();
            UpdateBatchEntriesState(animate: false);
            RegenerateSingle();
        }

        // =================================================================
        //  Sidebar + mode
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;

            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            ModeTogglePanel.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;

            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private bool IsBatchMode => RadioModeBatch.IsChecked == true;

        private void RadioMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;

            bool batch = IsBatchMode;
            UiMotion.FadeVisibility(SingleModeArea, !batch);
            UiMotion.FadeVisibility(BatchModeArea, batch);
            UiMotion.FadeVisibility(SingleInputSection, !batch);
            UiMotion.FadeVisibility(BatchInputSection, batch);

            if (!batch)
                RegenerateSingle();
        }

        // =================================================================
        //  Input (single mode)
        // =================================================================

        private BarcodeInputMode CurrentInputMode()
        {
            if (RadioInputNumeric.IsChecked == true) return BarcodeInputMode.Numeric;
            if (RadioInputFile.IsChecked == true) return BarcodeInputMode.File;
            return BarcodeInputMode.Text;
        }

        private void RadioInputMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;

            bool isFile = CurrentInputMode() == BarcodeInputMode.File;
            UiMotion.FadeVisibility(ContentTextBox, !isFile);
            UiMotion.FadeVisibility(TxtCharCount, !isFile);
            UiMotion.FadeVisibility(PathPickerGrid, isFile);

            RegenerateSingle();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "Select a file to encode" };
            if (dialog.ShowDialog() == true)
                PathTextBox.Text = dialog.FileName; // TextChanged -> RegenerateSingle
        }

        private void ContentInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            RegenerateSingle();
        }

        // =================================================================
        //  Symbology
        // =================================================================

        private BarcodeSymbology CurrentSymbology()
        {
            RadioButton? checkedType = new[] { RadioTypeCode128, RadioTypeCode39, RadioTypeEan13, RadioTypeItf }
                .FirstOrDefault(r => r.IsChecked == true);

            return checkedType?.Tag is string tag && Enum.TryParse(tag, out BarcodeSymbology sym)
                ? sym
                : BarcodeSymbology.Code128;
        }

        private void RadioType_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateTypeHint();
            RegenerateSingle();
        }

        private void UpdateTypeHint() =>
            TxtTypeHint.Text = BarcodeManager.DescribeSymbology(CurrentSymbology());

        // =================================================================
        //  Size
        // =================================================================

        private static bool TryReadPositive(TextBox box, out int value) =>
            int.TryParse(box.Text.Trim(), out value) && value > 0 && value <= 10000;

        private void SizeInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;

            SetFieldError(WidthBar, TxtWidthError, !TryReadPositive(WidthTextBox, out _));
            SetFieldError(HeightBar, TxtHeightError, !TryReadPositive(HeightTextBox, out _));
            RegenerateSingle();
        }

        private void BtnSizePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag }) return;

            string[] parts = tag.Split('x', 'X');
            if (parts.Length != 2) return;

            // Setting both fires SizeInput_Changed twice; the second call
            // sees the final values, so the preview ends up correct.
            WidthTextBox.Text = parts[0];
            HeightTextBox.Text = parts[1];
        }

        private static void SetFieldError(Border bar, FrameworkElement message, bool hasError)
        {
            if (hasError)
                bar.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");
            else
                bar.ClearValue(Border.BorderBrushProperty);

            message.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        }

        // =================================================================
        //  Colours
        // =================================================================

        private static string? NormalizeHex(string text)
        {
            string t = text.Trim();
            if (!HexPattern.IsMatch(t)) return null;
            return ("#" + t.TrimStart('#')).ToUpperInvariant();
        }

        private string CurrentForegroundHex => NormalizeHex(TxtForegroundHex.Text) ?? DefaultForeground;
        private string CurrentBackgroundHex => NormalizeHex(TxtBackgroundHex.Text) ?? DefaultBackground;

        private void ForegroundHex_TextChanged(object sender, TextChangedEventArgs e) =>
            OnHexChanged(TxtForegroundHex, ForegroundBar, TxtForegroundHexError);

        private void BackgroundHex_TextChanged(object sender, TextChangedEventArgs e) =>
            OnHexChanged(TxtBackgroundHex, BackgroundBar, TxtBackgroundHexError);

        private void OnHexChanged(TextBox box, Border bar, FrameworkElement error)
        {
            if (!_viewConstructed) return;

            bool valid = NormalizeHex(box.Text) != null;
            SetFieldError(bar, error, !valid);
            if (!valid) return;

            UpdateSwatches();
            UpdateColorWarning();
            RegenerateSingle();
        }

        /// <summary>Tidies a valid hex to "#RRGGBB" when the field loses focus.</summary>
        private void ColorField_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box && NormalizeHex(box.Text) is string normalized && box.Text != normalized)
                box.Text = normalized;
        }

        private void UpdateSwatches()
        {
            SwatchForeground.Background = new SolidColorBrush(HexToColor(CurrentForegroundHex));
            SwatchBackground.Background = new SolidColorBrush(HexToColor(CurrentBackgroundHex));
        }

        private void UpdateColorWarning()
        {
            string? warning = BarcodeManager.GetColorWarning(CurrentForegroundHex, CurrentBackgroundHex);
            if (warning != null)
                TxtColorWarning.Text = warning;
            UiMotion.FadeVisibility(ColorWarningPanel, warning != null);
        }

        private void SwatchForeground_Click(object sender, RoutedEventArgs e)
        {
            if (PickColor(CurrentForegroundHex) is string hex)
                TxtForegroundHex.Text = hex;
        }

        private void SwatchBackground_Click(object sender, RoutedEventArgs e)
        {
            if (PickColor(CurrentBackgroundHex) is string hex)
                TxtBackgroundHex.Text = hex;
        }

        private static string? PickColor(string currentHex)
        {
            Color c = HexToColor(currentHex);
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B),
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return null;

            var picked = dialog.Color;
            return $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
        }

        private static Color HexToColor(string hex)
        {
            string h = hex.TrimStart('#');
            return Color.FromRgb(
                Convert.ToByte(h[..2], 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16));
        }

        private void BtnSwapColors_Click(object sender, RoutedEventArgs e)
        {
            string fg = CurrentForegroundHex, bg = CurrentBackgroundHex;
            TxtForegroundHex.Text = bg;
            TxtBackgroundHex.Text = fg;
        }

        private void BtnResetColors_Click(object sender, RoutedEventArgs e)
        {
            TxtForegroundHex.Text = DefaultForeground;
            TxtBackgroundHex.Text = DefaultBackground;
        }

        // =================================================================
        //  Options + export format
        // =================================================================

        private void ShowTextCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            RegenerateSingle();
        }

        /// <summary>The format a Save / Export menu item stands for (its Tag is the enum name).</summary>
        private static bool TryGetExportFormat(object sender, out BarcodeExportFormat format)
        {
            format = BarcodeExportFormat.Png;
            return sender is MenuItem { Tag: string tag } && Enum.TryParse(tag, out format);
        }

        /// <summary>Returns null and fills <paramref name="error"/> when the sidebar has invalid values.</summary>
        private BarcodeOptions? TryBuildOptions(out string error)
        {
            if (!TryReadPositive(WidthTextBox, out int width) || !TryReadPositive(HeightTextBox, out int height))
            {
                error = "Width and height must be whole numbers between 1 and 10000.";
                return null;
            }

            if (NormalizeHex(TxtForegroundHex.Text) is null || NormalizeHex(TxtBackgroundHex.Text) is null)
            {
                error = "Colours must be hex values like #1A2B3C.";
                return null;
            }

            error = "";
            return new BarcodeOptions
            {
                WidthPx = width,
                HeightPx = height,
                ForegroundHex = CurrentForegroundHex,
                BackgroundHex = CurrentBackgroundHex,
                ShowText = ShowTextCheckBox.IsChecked == true,
            };
        }

        // =================================================================
        //  Single mode: live preview
        // =================================================================

        private void RegenerateSingle()
        {
            if (!_viewConstructed || !IsLoaded || IsBatchMode) return;

            BarcodeInputMode mode = CurrentInputMode();
            string data = mode == BarcodeInputMode.File ? PathTextBox.Text : ContentTextBox.Text;

            if (mode != BarcodeInputMode.File)
                TxtCharCount.Text = data.Length == 1 ? "1 character" : $"{data.Length} characters";

            if (string.IsNullOrWhiteSpace(data))
            {
                ShowPreviewError(mode == BarcodeInputMode.File
                    ? "Choose a file - its path will be encoded"
                    : "Type a value to generate a barcode");
                return;
            }

            if (mode == BarcodeInputMode.Numeric && !data.All(char.IsAsciiDigit))
            {
                ShowPreviewError("Numeric mode accepts digits only");
                return;
            }

            if (TryBuildOptions(out string optionsError) is not BarcodeOptions options)
            {
                ShowPreviewError(optionsError);
                return;
            }

            BarcodeSymbology symbology = CurrentSymbology();
            BarcodeGenerationResult result = _barcode.Generate(data, symbology, options);

            if (!result.Success || result.Image is null)
            {
                ShowPreviewError(result.ErrorMessage);
                return;
            }

            bool wasEmpty = _currentResult is null;
            _currentResult = result;

            PreviewImage.Source = result.Image;
            PreviewLabel.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = true;
            CopyButton.IsEnabled = true;

            TxtPreviewInfo.Text =
                $"{BarcodeNames.Display(symbology)} · {result.PixelWidth} × {result.PixelHeight} px · " +
                (result.EncodedText.Length == 1 ? "1 character" : $"{result.EncodedText.Length} characters");

            string status = result.EncodedText != data.Trim() && symbology == BarcodeSymbology.Ean13
                ? $"Ready - check digit added: {result.EncodedText}"
                : "Ready";
            SetStatus(status, StatusKind.Success);

            // Animate only the empty -> image transition; live edits stay still.
            if (wasEmpty)
                UiMotion.FadeIn(PreviewLabel);
        }

        private void ShowPreviewError(string message)
        {
            _currentResult = null;
            PreviewImage.Source = null;
            PreviewLabel.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = false;
            CopyButton.IsEnabled = false;
            TxtPreviewInfo.Text = "";

            if (PreviewPlaceholder.Visibility != Visibility.Visible)
            {
                PreviewPlaceholder.Visibility = Visibility.Visible;
                UiMotion.FadeIn(PreviewPlaceholder);
            }

            SetStatus(message, StatusKind.Error);
        }

        private void SetStatus(string text, StatusKind kind)
        {
            TxtProgress.Text = text;
            TxtProgress.ToolTip = text;

            (string bg, string fg, PackIconKind icon) = kind switch
            {
                StatusKind.Success => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.CheckCircleOutline),
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                _ => ("RowBg", "TextSecondary", PackIconKind.InformationOutline),
            };

            BorderStatus.SetResourceReference(Border.BackgroundProperty, bg);
            TxtProgress.SetResourceReference(TextBlock.ForegroundProperty, fg);
            IconStatus.Kind = icon;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResult?.Image is null) return;

            try
            {
                Clipboard.SetImage(_currentResult.Image);
                SetStatus("Copied to clipboard", StatusKind.Success);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_Err_CouldntCopy", "Couldn't copy: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MenuSave_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResult is null || !TryGetExportFormat(sender, out BarcodeExportFormat format)) return;

            string ext = BarcodeManager.ExtensionFor(format);
            var dialog = new SaveFileDialog
            {
                Title = "Save barcode",
                Filter = BarcodeManager.FileFilterFor(format),
                DefaultExt = ext,
                FileName = ZxingImageHelpers.SanitizeFileName(_currentResult.EncodedText, 1, "barcode") + ext,
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                BarcodeManager.SaveToFile(_currentResult, dialog.FileName, format);
                SetStatus($"Saved {Path.GetFileName(dialog.FileName)}", StatusKind.Success);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_Err_CouldntSave", "Couldn't save: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =================================================================
        //  Batch entries
        // =================================================================

        private void BatchEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
            UpdateBatchEntriesState(animate: true);

        private void UpdateBatchEntriesState(bool animate)
        {
            int count = _batchEntries.Count;
            UiMotion.FadeVisibility(BatchEntriesEmptyPanel, count == 0);
            BatchCountPill.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtBatchCount.Text = count.ToString("N0");
            BtnClearBatchEntries.IsEnabled = count > 0 && !_isBatchGenerating;

            if (animate && count > 0)
                UiMotion.Pop(BatchCountPill);
        }

        /// <summary>Adds several entries with a single visual update.</summary>
        private void AddEntries(IEnumerable<string> values)
        {
            var clean = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            if (clean.Count == 0) return;

            // Detach while adding so a 10k paste doesn't re-layout 10k times.
            _batchEntries.CollectionChanged -= BatchEntries_CollectionChanged;
            ListBoxBatchEntries.ItemsSource = null;
            foreach (string v in clean)
                _batchEntries.Add(v);
            ListBoxBatchEntries.ItemsSource = _batchEntries;
            _batchEntries.CollectionChanged += BatchEntries_CollectionChanged;

            UpdateBatchEntriesState(animate: true);
        }

        private void TxtNewBatchEntry_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            AddTypedEntry();
            e.Handled = true;
        }

        private void BtnAddBatchEntry_Click(object sender, RoutedEventArgs e) => AddTypedEntry();

        private void AddTypedEntry()
        {
            string value = TxtNewBatchEntry.Text.Trim();
            if (value.Length == 0) return;

            _batchEntries.Add(value);
            TxtNewBatchEntry.Clear();
            TxtNewBatchEntry.Focus();
        }

        private void BtnPasteBatchEntries_Click(object sender, RoutedEventArgs e)
        {
            string text;
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_Err_ClipboardRead", "Couldn't read the clipboard: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (text.Length == 0)
            {
                MessageBox.Show(Loc.T("Str_MB_TheClipboardHasNo2", "The clipboard has no text. Copy a column from Excel or a list of lines first."),
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AddEntries(text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private void BtnLoadNamesFromFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Choose a folder - each file name becomes an entry" };
            if (dialog.ShowDialog() != true) return;

            try
            {
                AddEntries(Directory.EnumerateFiles(dialog.FolderName)
                    .Select(Path.GetFileName)
                    .OfType<string>());
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_Err_FolderRead", "Couldn't read that folder: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRemoveBatchEntry_Click(object sender, RoutedEventArgs e)
        {
            var selected = ListBoxBatchEntries.SelectedItems.Cast<string>().ToList();
            if (selected.Count == 0) return;

            int firstIndex = ListBoxBatchEntries.SelectedIndex;
            foreach (string item in selected)
                _batchEntries.Remove(item);

            // Keep keyboard flow: select the row that slid into place.
            if (_batchEntries.Count > 0)
                ListBoxBatchEntries.SelectedIndex = Math.Min(firstIndex, _batchEntries.Count - 1);
        }

        private void BtnClearBatchEntries_Click(object sender, RoutedEventArgs e)
        {
            if (_batchEntries.Count == 0) return;

            if (_batchEntries.Count > 20 &&
                MessageBox.Show(Loc.F("Str_MB_RemoveAllEntries", "Remove all {0:N0} entries?", _batchEntries.Count), DialogTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _batchEntries.Clear();
        }

        // =================================================================
        //  Batch run
        // =================================================================

        private async void BtnGenerateAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchGenerating)
            {
                _batchCts?.Cancel();
                return;
            }

            if (_batchEntries.Count == 0)
            {
                MessageBox.Show(Loc.T("Str_MB_AddOrLoadAt", "Add or load at least one value first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (TryBuildOptions(out string optionsError) is not BarcodeOptions options)
            {
                MessageBox.Show(optionsError, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var inputs = _batchEntries.ToList();
            BarcodeSymbology symbology = CurrentSymbology();

            _batchCts = new CancellationTokenSource();
            SetBatchRunning(true);
            ReportBatchProgress(0, inputs.Count, "Generating");

            var progress = new Progress<(int Done, int Total)>(p => ReportBatchProgress(p.Done, p.Total, "Generating"));

            try
            {
                List<BatchBarcodeRow> rows = await _barcode.GenerateBatchAsync(inputs, symbology, options, progress, _batchCts.Token);
                ShowBatchResults(rows);
            }
            catch (OperationCanceledException)
            {
                TxtBatchProgress.Text = "Cancelled - nothing was kept";
                ProgressBarBatch.Value = 0;
                TxtProgressPercent.Text = "0%";
            }
            catch (Exception ex)
            {
                TxtBatchProgress.Text = "Batch failed";
                MessageBox.Show(Loc.F("Str_MB_BatchGenerationFailed", "Batch generation failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _batchCts.Dispose();
                _batchCts = null;
                SetBatchRunning(false);
            }
        }

        private void ReportBatchProgress(int done, int total, string verb)
        {
            double percent = total == 0 ? 0 : done * 100.0 / total;
            ProgressBarBatch.Value = percent;
            TxtProgressPercent.Text = $"{percent:0}%";
            TxtBatchProgress.Text = $"{verb} {done:N0} of {total:N0}";
        }

        private void SetBatchRunning(bool running)
        {
            _isBatchGenerating = running;

            IconGenerateAll.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.PlayCircleOutline;
            if (running)
                TxtGenerateAllLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else
                TxtGenerateAllLabel.SetResourceReference(TextBlock.TextProperty, "Str_Barcode_GenerateAll");

            // Lock the inputs that would change the run underneath it.
            BtnAddBatchEntry.IsEnabled = !running;
            BtnPasteBatchEntries.IsEnabled = !running;
            BtnLoadNamesFromFolder.IsEnabled = !running;
            BtnRemoveBatchEntry.IsEnabled = !running;
            BtnClearBatchEntries.IsEnabled = !running && _batchEntries.Count > 0;
            RadioModeSingle.IsEnabled = !running;
            BatchExportButton.IsEnabled = !running && !_isExporting && HasExportableRows();
        }

        private bool HasExportableRows() =>
            _batchRows?.Any(r => r.Status == BarcodeRowStatus.Generated) == true;

        private void ShowBatchResults(List<BatchBarcodeRow> rows)
        {
            _batchRows = rows;
            DataGridBatch.ItemsSource = rows;

            int ok = rows.Count(r => r.Status == BarcodeRowStatus.Generated);
            int failed = rows.Count - ok;

            UiMotion.FadeVisibility(BatchEmptyStatePanel, rows.Count == 0);

            TxtSummaryCount.Text = $"{ok:N0} of {rows.Count:N0} generated";
            SummaryPill.Visibility = Visibility.Visible;
            UiMotion.Pop(SummaryPill);

            TxtBatchProgress.Text = failed == 0
                ? $"Done - all {rows.Count:N0} generated"
                : $"Done - {failed:N0} failed (see the Message column)";
        }

        private async void MenuBatchExport_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetExportFormat(sender, out BarcodeExportFormat format)) return;
            if (_batchRows is null || !HasExportableRows())
            {
                MessageBox.Show(Loc.T("Str_MB_NothingToExportYet2", "Nothing to export yet - click Generate All first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFolderDialog { Title = "Choose where to save the barcodes" };
            if (dialog.ShowDialog() != true) return;

            int total =_batchRows.Count(r => r.Status == BarcodeRowStatus.Generated);

            _isExporting = true;
            BatchExportButton.IsEnabled = false;
            BtnGenerateAll.IsEnabled = false;
            _batchCts = new CancellationTokenSource();
            ReportBatchProgress(0, total, "Exporting");

            var progress = new Progress<(int Done, int Total)>(p => ReportBatchProgress(p.Done, p.Total, "Exporting"));

            try
            {
                int written = await _barcode.ExportBatchAsync(_batchRows, dialog.FolderName, format, progress, _batchCts.Token);
                TxtBatchProgress.Text = $"Exported {written:N0} files";
                Smart_Tools.WPF.Controllers.ToastNotificationManager.ShowToast(DialogTitle,
                    $"Exported {written:N0} of {total:N0} barcodes to {dialog.FolderName}");
            }
            catch (OperationCanceledException)
            {
                TxtBatchProgress.Text = "Export cancelled";
            }
            catch (Exception ex)
            {
                TxtBatchProgress.Text = "Export failed";
                MessageBox.Show(Loc.F("Str_Err_ExportFailed", "Export failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _batchCts.Dispose();
                _batchCts = null;
                _isExporting = false;
                BtnGenerateAll.IsEnabled = true;
                BatchExportButton.IsEnabled = HasExportableRows();
            }
        }
    }
}
