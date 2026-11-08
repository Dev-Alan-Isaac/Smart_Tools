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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    /// QR Code Generator view - UI only. Encoding, rendering, batch work and
    /// export live in <see cref="QrCodeManager"/>; data shapes in
    /// QrCodeModel.cs. Same layout and behaviour as BarCodeGeneratorView.
    ///
    /// Single mode regenerates live (debounced ~120 ms so typing stays
    /// smooth even at print sizes). Batch mode runs on the manager's STA
    /// worker with progress + cancel, keeps thumbnails only, and re-renders
    /// at full size on export.
    /// </summary>
    public partial class QrCodeGeneratorView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DefaultForeground = "#000000";
        private const string DefaultBackground = "#FFFFFF";
        private static string DialogTitle => Loc.T("Str_Tool_qr-generator_Name", "QR Code Generator");
        private const int MaxLogoDecodePx = 1024;

        private static readonly Regex HexPattern = new("^#?[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

        private readonly QrCodeManager _qr = new();
        private readonly Smart_Tools.WPF.Models.BulkObservableCollection<string> _batchEntries = new();
        private readonly ObservableCollection<BatchEntrySource> _sources = new();
        private readonly Dictionary<BatchEntrySource, List<string>> _sourceEntries = new();
        private readonly DispatcherTimer _regenTimer;

        private QrCodeGenerationResult? _currentResult;
        private List<BatchQrCodeRow>? _batchRows;
        private CancellationTokenSource? _batchCts;
        private bool _isBatchGenerating;
        private bool _isExporting;
        private bool _optionsCollapsed;

        private BitmapSource? _logoImage;
        private double _logoScale = 0.22;

        // File mode: the chosen file's text (null = nothing usable loaded).
        private string? _fileContent;

        private readonly bool _viewConstructed;

        private enum StatusKind { Warning, Success, Error }

        public QrCodeGeneratorView()
        {
            InitializeComponent();

            _regenTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(120),
            };
            _regenTimer.Tick += (_, _) =>
            {
                _regenTimer.Stop();
                RegenerateSingle();
            };

            InputCard.ItemsSource = _sources;
            InputCard.FileFilterProvider = () => Loc.T("Str_Generators_MsgEntryFilter", "Lists of codes|*.csv;*.tsv;*.json;*.xml;*.txt;*.list|All files|*.*");
            _sources.CollectionChanged += (_, _) => InputCard.SetEmpty(_sources.Count == 0);
            InputCard.SetEmpty(true);
            ListBoxBatchEntries.ItemsSource = _batchEntries;
            _batchEntries.CollectionChanged += BatchEntries_CollectionChanged;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing.</summary>
        public void CancelActiveRun()
        {
            _regenTimer.Stop();
            _batchCts?.Cancel();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateExportHints();
            UpdateSwatches();
            UpdateColorWarning();
            UpdateBatchEntriesState(animate: false);
            RegenerateSingle();
        }

        /// <summary>Coalesces bursts of changes (typing, slider drags) into one render.</summary>
        private void ScheduleRegenerate()
        {
            if (!_viewConstructed || !IsLoaded) return;
            _regenTimer.Stop();
            _regenTimer.Start();
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
            UiMotion.FadeVisibility(InputCard, batch);

            if (!batch)
                RegenerateSingle();
        }

        // =================================================================
        //  Content (single mode)
        // =================================================================

        private bool IsFileMode => RadioInputFile.IsChecked == true;

        private void InputMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;

            bool isFile = IsFileMode;
            UiMotion.FadeVisibility(TextInputPanel, !isFile);
            UiMotion.FadeVisibility(FileInputPanel, isFile);
            TxtValidationMessage.Visibility = Visibility.Collapsed;

            RegenerateSingle();
        }

        private void BtnTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag } ||
                !Enum.TryParse(tag, out QrContentTemplate template))
                return;

            TxtInput.Text = QrCodeManager.TemplateFor(template);
            TxtInput.Focus();
            TxtInput.SelectAll(); // ready to overwrite the example
        }

        private void TxtInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;

            string text = TxtInput.Text;
            TxtCharCount.Text = text.Length == 1 ? "1 character" : $"{text.Length:N0} characters";
            TxtContentKind.Text = text.Trim().Length == 0 ? "" : QrCodeManager.DescribeContent(text);

            ScheduleRegenerate();
        }

        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Str_QrGen_MsgPickTextFile", "Choose a text file to encode"),
                Filter = Loc.T("Str_QrGen_MsgTextFilter", "Text and contact files|*.txt;*.vcf;*.ics;*.csv;*.json;*.xml;*.md|All files|*.*"),
            };
            if (dialog.ShowDialog() != true) return;

            TxtFilePath.Text = dialog.FileName;
            TxtFilePath.ToolTip = dialog.FileName;

            if (QrCodeManager.TryReadFileForEncoding(dialog.FileName, out string text, out string error))
            {
                _fileContent = text;
                TxtValidationMessage.Visibility = Visibility.Collapsed;
            }
            else
            {
                _fileContent = null;
                TxtValidationMessage.Text = error;
                UiMotion.FadeVisibility(TxtValidationMessage, true);
            }

            RegenerateSingle();
        }

        // =================================================================
        //  Size
        // =================================================================

        private static bool TryReadInt(TextBox box, int min, int max, out int value) =>
            int.TryParse(box.Text.Trim(), out value) && value >= min && value <= max;

        private void SizeField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            ValidateSizeFields();
            ScheduleRegenerate();
        }

        /// <summary>Tidies whitespace ("  512 " -> "512") once the user leaves the field.</summary>
        private void SizeField_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box && int.TryParse(box.Text.Trim(), out int v) && box.Text != v.ToString())
                box.Text = v.ToString();
        }

        /// <returns>The first problem, or null when all three fields are valid.</returns>
        private string? ValidateSizeFields()
        {
            int max = QrCodeManager.MaxDimensionPx;
            bool widthOk = TryReadInt(TxtWidth, 1, max, out _);
            bool heightOk = TryReadInt(TxtHeight, 1, max, out _);
            bool marginOk = TryReadInt(TxtMargin, 0, 20, out _);

            SetFieldError(WidthBar, !widthOk);
            SetFieldError(HeightBar, !heightOk);
            SetFieldError(MarginBar, !marginOk);

            string? error =
                !widthOk ? $"Width must be a whole number from 1 to {max}." :
                !heightOk ? $"Height must be a whole number from 1 to {max}." :
                !marginOk ? "Quiet zone must be 0-20 modules (4 is the QR standard)." :
                null;

            TxtSizeError.Text = error ?? "";
            TxtSizeError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            return error;
        }

        private void BtnSizePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag }) return;

            string[] parts = tag.Split('x', 'X');
            if (parts.Length != 2) return;

            TxtWidth.Text = parts[0];
            TxtHeight.Text = parts[1];
        }

        private static void SetFieldError(Border bar, bool hasError)
        {
            if (hasError)
                bar.SetResourceReference(Border.BorderBrushProperty, "StatusErrorFg");
            else
                bar.ClearValue(Border.BorderBrushProperty);
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

        private void ColorField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;

            bool fgOk = NormalizeHex(TxtForegroundHex.Text) != null;
            bool bgOk = NormalizeHex(TxtBackgroundHex.Text) != null;
            SetFieldError(ForegroundBar, !fgOk);
            SetFieldError(BackgroundBar, !bgOk);
            TxtColorError.Visibility = fgOk && bgOk ? Visibility.Collapsed : Visibility.Visible;

            if (!fgOk || !bgOk)
            {
                ScheduleRegenerate(); // shows the error in the status pill
                return;
            }

            UpdateSwatches();
            UpdateColorWarning();
            ScheduleRegenerate();
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
            string? warning = QrCodeManager.GetColorWarning(CurrentForegroundHex, CurrentBackgroundHex);
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
        //  Error correction
        // =================================================================

        private QrErrorCorrection CurrentErrorCorrection()
        {
            RadioButton? checkedLevel = new[] { RadioEcLow, RadioEcMedium, RadioEcQuartile, RadioEcHigh }
                .FirstOrDefault(r => r.IsChecked == true);

            return checkedLevel?.Tag is string tag && Enum.TryParse(tag, out QrErrorCorrection level)
                ? level
                : QrErrorCorrection.Medium;
        }

        private void RadioErrorCorrection_Checked(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            RegenerateSingle();
        }

        // =================================================================
        //  Logo
        // =================================================================

        private void BtnBrowseLogo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Str_QrGen_MsgPickLogo", "Choose a logo image"),
                Filter = Loc.T("Str_QrGen_MsgImageFilter", "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*"),
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                BitmapSource logo = LoadLogo(dialog.FileName);

                _logoImage = logo;
                LogoThumbnail.Source = logo;
                LogoEmptyIcon.Visibility = Visibility.Collapsed;
                TxtLogoName.Text = Path.GetFileName(dialog.FileName);
                TxtLogoName.ToolTip = dialog.FileName;
                BtnRemoveLogo.IsEnabled = true;
                SliderLogoScale.IsEnabled = true;
                ErrorCorrectionPanel.IsEnabled = false; // a logo locks the level to High
                UiMotion.FadeVisibility(TxtErrorCorrectionNote, true);
                UiMotion.Pop(LogoTileButton);

                UpdateExportHints();
                RegenerateSingle();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_Err_ImageLoad", "Couldn't load that image: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Loads (and, for huge images, downsizes) a logo; frozen so the batch worker can use it.</summary>
        private static BitmapSource LoadLogo(string path)
        {
            BitmapImage Load(int decodeWidth)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }

            var logo = Load(0);
            return logo.PixelWidth > MaxLogoDecodePx ? Load(MaxLogoDecodePx) : logo;
        }

        private void BtnRemoveLogo_Click(object sender, RoutedEventArgs e)
        {
            _logoImage = null;
            LogoThumbnail.Source = null;
            LogoEmptyIcon.Visibility = Visibility.Visible;
            TxtLogoName.SetResourceReference(TextBlock.TextProperty, "Str_QRcodeGenerator_PngWithTransparencyWorks");
            TxtLogoName.ToolTip = null;
            BtnRemoveLogo.IsEnabled = false;
            SliderLogoScale.IsEnabled = false;
            ErrorCorrectionPanel.IsEnabled = true;
            UiMotion.FadeVisibility(TxtErrorCorrectionNote, false);

            UpdateExportHints();
            RegenerateSingle();
        }

        private void SliderLogoScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _logoScale = e.NewValue;
            if (!_viewConstructed) return;

            TxtLogoScaleValue.Text = $"{e.NewValue * 100:0}%";
            if (_logoImage != null)
                ScheduleRegenerate();
        }

        // =================================================================
        //  Caption + export format
        // =================================================================

        private void OptionToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            RegenerateSingle();
        }

        /// <summary>The format a Save / Export menu item stands for (its Tag is the enum name).</summary>
        private static bool TryGetExportFormat(object sender, out QrExportFormat format)
        {
            format = QrExportFormat.Png;
            return sender is MenuItem { Tag: string tag } && Enum.TryParse(tag, out format);
        }

        private void UpdateExportHints()
        {
            TxtBatchExportInfo.Text = _logoImage != null
                ? Loc.T("Str_QrGen_MsgExportHintLogo", "Each generated code is saved as an image file, with your logo")
                : Loc.T("Str_QrGen_MsgExportHint", "Each generated code is saved as an image file");
        }

        /// <summary>Builds options from the sidebar, or returns null with the first problem.</summary>
        private QrCodeOptions? TryBuildOptions(out string error)
        {
            if (ValidateSizeFields() is string sizeError)
            {
                error = sizeError;
                return null;
            }

            if (NormalizeHex(TxtForegroundHex.Text) is null || NormalizeHex(TxtBackgroundHex.Text) is null)
            {
                error = "Colours must be hex values like #1A2B3C.";
                return null;
            }

            TryReadInt(TxtWidth, 1, QrCodeManager.MaxDimensionPx, out int width);
            TryReadInt(TxtHeight, 1, QrCodeManager.MaxDimensionPx, out int height);
            TryReadInt(TxtMargin, 0, 20, out int margin);

            error = "";
            return new QrCodeOptions
            {
                WidthPx = width,
                HeightPx = height,
                QuietZoneModules = margin,
                ForegroundHex = CurrentForegroundHex,
                BackgroundHex = CurrentBackgroundHex,
                ErrorCorrection = CurrentErrorCorrection(),
                LogoImage = _logoImage,
                LogoScale = _logoScale,
                ShowText = ShowTextCheckBox.IsChecked == true,
            };
        }

        // =================================================================
        //  Single mode: live preview
        // =================================================================

        private void RegenerateSingle()
        {
            _regenTimer.Stop();
            if (!_viewConstructed || !IsLoaded || IsBatchMode) return;

            string? data = IsFileMode ? _fileContent : TxtInput.Text;

            if (string.IsNullOrWhiteSpace(data))
            {
                ShowPreviewError(IsFileMode
                    ? (TxtFilePath.Text.Length == 0 ? "Choose a file - its text will be encoded" : "That file can't be encoded")
                    : "Type a link or some text to create a QR code");
                return;
            }

            if (TryBuildOptions(out string optionsError) is not QrCodeOptions options)
            {
                ShowPreviewError(optionsError);
                return;
            }

            QrCodeGenerationResult result = _qr.Generate(data, options);
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
            BtnSaveAs.IsEnabled = true;
            BtnCopyToClipboard.IsEnabled = true;

            TxtPreviewInfo.Text = Loc.F("Str_QrGen_MsgPreviewInfo", "{0} · version {1} · {2}×{2} modules · {3} × {4} px · {5} px per module",
                result.ContentKind, result.Version, result.ModuleCount, result.PixelWidth, result.PixelHeight, result.ModulePx);

            string ec = result.EffectiveErrorCorrection.ToString();
            string status = result.ModulePx < 3
                ? Loc.F("Str_QrGen_MsgReadySmall", "Ready - small modules ({0} px); make it larger for print", result.ModulePx)
                : _logoImage != null ? Loc.F("Str_QrGen_MsgReadyLogo", "Ready - {0} error correction (logo)", ec) : Loc.F("Str_QrGen_MsgReadyEc", "Ready - {0} error correction", ec);
            SetStatus(status, result.ModulePx < 3 ? StatusKind.Warning : StatusKind.Success);

            // Animate only the empty -> image transition; live edits stay still.
            if (wasEmpty)
                UiMotion.FadeIn(PreviewLabel);
        }

        private void ShowPreviewError(string message)
        {
            _currentResult = null;
            PreviewImage.Source = null;
            PreviewLabel.Visibility = Visibility.Collapsed;
            BtnSaveAs.IsEnabled = false;
            BtnCopyToClipboard.IsEnabled = false;
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
                _ => ("StatusWarningBg", "StatusWarningFg", PackIconKind.InformationOutline),
            };

            BorderStatus.SetResourceReference(Border.BackgroundProperty, bg);
            TxtProgress.SetResourceReference(TextBlock.ForegroundProperty, fg);
            IconStatus.Kind = icon;
        }

        private void BtnCopyToClipboard_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResult?.Image is null) return;

            try
            {
                Clipboard.SetImage(_currentResult.Image);
                SetStatus(Loc.T("Str_Generators_MsgCopied", "Copied to clipboard"), StatusKind.Success);
                UiMotion.Pop(BorderStatus);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Str_Err_CouldntCopy", "Couldn't copy: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MenuSave_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResult is null || !TryGetExportFormat(sender, out QrExportFormat format)) return;

            string ext = QrCodeManager.ExtensionFor(format);
            var dialog = new SaveFileDialog
            {
                Title = Loc.T("Str_QrGen_MsgSaveTitle", "Save QR code"),
                Filter = QrCodeManager.FileFilterFor(format),
                DefaultExt = ext,
                FileName = "qrcode" + ext,
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                QrCodeManager.SaveToFile(_currentResult, dialog.FileName, format);
                SetStatus(Loc.F("Str_Generators_MsgSaved", "Saved {0}", Path.GetFileName(dialog.FileName)), StatusKind.Success);
                UiMotion.Pop(BorderStatus);
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

            // One entry per line; tabs too, so a multi-column Excel copy works.
            AddEntries(text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        // ---- files and folders in the Input card ----

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e) => _ = AddSourcesAsync(e.Paths);

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_isBatchGenerating) return;
            foreach (var source in _sources.ToList()) DropSource(source);
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchGenerating || (sender as FrameworkElement)?.Tag is not BatchEntrySource source) return;
            DropSource(source);
        }

        /// <summary>Takes a source off the list together with the codes it gave.</summary>
        private void DropSource(BatchEntrySource source)
        {
            if (_sourceEntries.Remove(source, out var entries))
            {
                _batchEntries.CollectionChanged -= BatchEntries_CollectionChanged;
                ListBoxBatchEntries.ItemsSource = null;
                _batchEntries.RemoveOccurrences(entries);
                ListBoxBatchEntries.ItemsSource = _batchEntries;
                _batchEntries.CollectionChanged += BatchEntries_CollectionChanged;
                UpdateBatchEntriesState(animate: false);
            }
            _sources.Remove(source);
        }

        private async Task AddSourcesAsync(IEnumerable<string> paths)
        {
            var wanted = paths.Where(p => !_sources.Any(s => string.Equals(s.FullPath, p, StringComparison.OrdinalIgnoreCase))).ToList();
            var problems = new List<string>();
            int added = 0;

            foreach (string path in wanted)
            {
                bool folder = Directory.Exists(path);
                var (entries, error) = await Task.Run(() => folder ? BatchEntryReader.ReadFolderNames(path) : BatchEntryReader.ReadFile(path));
                if (error != null) { problems.Add(error); continue; }
                if (entries.Count == 0)
                {
                    problems.Add(Loc.F("Str_Generators_ErrSourceEmpty", "{0}: no codes found.", Path.GetFileName(path.TrimEnd('\\', '/'))));
                    continue;
                }

                var source = new BatchEntrySource { FullPath = path, IsFolder = folder, Count = entries.Count };
                _sources.Add(source);
                _sourceEntries[source] = entries;
                AddEntries(entries);
                added += entries.Count;
            }

            if (added > 0) TxtBatchProgress.Text = Loc.F("Str_Generators_MsgAddedCodes", "Added {0:N0} codes", added);
            if (problems.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, problems), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnRemoveBatchEntry_Click(object sender, RoutedEventArgs e)
        {
            var selected = ListBoxBatchEntries.SelectedItems.Cast<string>().ToList();
            if (selected.Count == 0) return;

            int firstIndex = ListBoxBatchEntries.SelectedIndex;
            _batchEntries.RemoveOccurrences(selected);

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
            _sourceEntries.Clear();
            _sources.Clear();
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

            if (TryBuildOptions(out string optionsError) is not QrCodeOptions options)
            {
                MessageBox.Show(optionsError, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var inputs = _batchEntries.ToList();

            _batchCts = new CancellationTokenSource();
            SetBatchRunning(true);
            ReportBatchProgress(0, inputs.Count, Loc.T("Str_Generators_VerbGenerating", "Generating"));

            var progress = new Progress<(int Done, int Total)>(p => ReportBatchProgress(p.Done, p.Total, Loc.T("Str_Generators_VerbGenerating", "Generating")));

            try
            {
                List<BatchQrCodeRow> rows = await _qr.GenerateBatchAsync(inputs, options, progress, _batchCts.Token);
                ShowBatchResults(rows);
            }
            catch (OperationCanceledException)
            {
                TxtBatchProgress.Text = Loc.T("Str_Generators_MsgBatchCancelled", "Cancelled - nothing was kept");
                ProgressBarBatch.Value = 0;
                TxtProgressPercent.Text = "0%";
            }
            catch (Exception ex)
            {
                TxtBatchProgress.Text = Loc.T("Str_Generators_MsgBatchFailed", "Batch failed");
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
            TxtBatchProgress.Text = Loc.F("Str_Generators_MsgProgress", "{0} {1:N0} of {2:N0}", verb, done, total);
        }

        private void SetBatchRunning(bool running)
        {
            _isBatchGenerating = running;

            IconGenerateAll.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.PlayCircleOutline;
            if (running)
                TxtGenerateAllLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else
                TxtGenerateAllLabel.SetResourceReference(TextBlock.TextProperty, "Str_QR_GenerateAll");

            // Lock the inputs that would change the run underneath it.
            BtnAddBatchEntry.IsEnabled = !running;
            BtnPasteBatchEntries.IsEnabled = !running;
            InputCard.IsLocked = running;
            BtnRemoveBatchEntry.IsEnabled = !running;
            BtnClearBatchEntries.IsEnabled = !running && _batchEntries.Count > 0;
            RadioModeSingle.IsEnabled = !running;
            BtnExportAll.IsEnabled = !running && !_isExporting && HasExportableRows();
        }

        private bool HasExportableRows() =>
            _batchRows?.Any(r => r.Status == QrRowStatus.Generated) == true;

        private void ShowBatchResults(List<BatchQrCodeRow> rows)
        {
            _batchRows = rows;
            DataGridBatch.ItemsSource = rows;

            int ok = rows.Count(r => r.Status == QrRowStatus.Generated);
            int failed = rows.Count - ok;

            UiMotion.FadeVisibility(BatchEmptyStatePanel, rows.Count == 0);

            TxtSummaryCount.Text = Loc.F("Str_Generators_MsgGeneratedOf", "{0:N0} of {1:N0} generated", ok, rows.Count);
            SummaryPill.Visibility = Visibility.Visible;
            UiMotion.Pop(SummaryPill);

            TxtBatchProgress.Text = failed == 0
                ? Loc.F("Str_Generators_MsgDoneAll", "Done - all {0:N0} generated", rows.Count)
                : Loc.F("Str_QrGen_MsgDoneFailed", "Done - {0:N0} failed (hover a status for the reason)", failed);
        }

        private async void MenuBatchExport_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetExportFormat(sender, out QrExportFormat format)) return;
            if (_batchRows is null || !HasExportableRows())
            {
                MessageBox.Show(Loc.T("Str_MB_NothingToExportYet2", "Nothing to export yet - click Generate All first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFolderDialog { Title = Loc.T("Str_QrGen_MsgPickExportFolder", "Choose where to save the QR codes") };
            if (dialog.ShowDialog() != true) return;

            int total =_batchRows.Count(r => r.Status == QrRowStatus.Generated);

            _isExporting = true;
            BtnExportAll.IsEnabled = false;
            BtnGenerateAll.IsEnabled = false;
            _batchCts = new CancellationTokenSource();
            ReportBatchProgress(0, total, Loc.T("Str_Generators_VerbExporting", "Exporting"));

            var progress = new Progress<(int Done, int Total)>(p => ReportBatchProgress(p.Done, p.Total, Loc.T("Str_Generators_VerbExporting", "Exporting")));

            try
            {
                int written = await _qr.ExportBatchAsync(_batchRows, dialog.FolderName, format, progress, _batchCts.Token);
                TxtBatchProgress.Text = Loc.F("Str_Generators_MsgExportedFiles", "Exported {0:N0} files", written);
                Smart_Tools.WPF.Controllers.ToastNotificationManager.ShowToast(DialogTitle,
                    Loc.F("Str_QrGen_MsgExportToast", "Exported {0:N0} of {1:N0} QR codes to {2}", written, total, dialog.FolderName));
            }
            catch (OperationCanceledException)
            {
                TxtBatchProgress.Text = Loc.T("Str_Generators_MsgExportCancelled", "Export cancelled");
            }
            catch (Exception ex)
            {
                TxtBatchProgress.Text = Loc.T("Str_Generators_MsgExportFailed", "Export failed");
                MessageBox.Show(Loc.F("Str_Err_ExportFailed", "Export failed: {0}", ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _batchCts.Dispose();
                _batchCts = null;
                _isExporting = false;
                BtnGenerateAll.IsEnabled = true;
                BtnExportAll.IsEnabled = HasExportableRows();
            }
        }
    }
}
