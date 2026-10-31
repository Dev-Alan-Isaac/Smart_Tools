using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// File Generator - UI only. Names, sizes, the free-space check, writing
    /// (under a temporary name, so a cancel never leaves a half file), the
    /// archive, the report and settings are in <see cref="FileGeneratorManager"/>.
    /// The output folder is kept for this session only.
    /// </summary>
    public partial class FileGeneratorView : UserControl
    {
        private const double SidebarExpandedWidth = 300;
        private const double SidebarCollapsedWidth = 56;
        private const string DialogTitle = "File Generator";

        private readonly BulkObservableCollection<FileGenItem> _items = new();
        private readonly DispatcherTimer _checkTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly bool _viewConstructed;

        private string _outputFolder => OutputCard.EffectiveFolder ?? "";   // chosen folder, else Downloads\Smart Tools\File Generator
        private CancellationTokenSource? _runCts;
        private FileGenSummary? _lastSummary;
        private FileGenFormat _format = FileGenFormat.Txt;
        private bool _isRunning;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private int _checkVersion;

        public FileGeneratorView()
        {
            InitializeComponent();
            DataGridResults.ItemsSource = _items;

            _checkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _checkTimer.Tick += (_, _) =>
            {
                _checkTimer.Stop();
                _ = UpdatePlanAsync();
            };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettings();
            };

            OutputCard.DefaultFolder = FileGeneratorManager.DefaultFolder;
            OutputCard.FolderChanged += OutputCard_FolderChanged;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a run (the file being written is deleted) and saves the options.</summary>
        public void CancelActiveRun()
        {
            try
            {
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
            ApplySettings(FileGeneratorManager.LoadSettings());
            UpdatePanels();
            SchedulePlan(immediately: true);
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
            ColTarget.Visibility = e.NewSize.Width < 950 ? Visibility.Collapsed : Visibility.Visible;

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

        // =================================================================
        //  Chips and fields
        // =================================================================

        private RadioButton[] FormatChipList => new[]
        {
            RadioFormatTxt, RadioFormatCsv, RadioFormatJson, RadioFormatXml, RadioFormatPdf, RadioFormatDocx,
            RadioFormatJpg, RadioFormatPng, RadioFormatMp4, RadioFormatBin, RadioFormatDat, RadioFormatCustom,
        };

        private RadioButton[] FillChipList => new[] { RadioFillZeroes, RadioFillRandom, RadioFillPattern, RadioFillLorem };
        private RadioButton[] ArchiveChipList => new[] { RadioArchiveNone, RadioArchiveZip, RadioArchiveTar };

        private static T ChipValue<T>(IEnumerable<RadioButton> chips, T fallback) where T : struct, Enum =>
            chips.FirstOrDefault(c => c.IsChecked == true)?.Tag is string tag && Enum.TryParse(tag, out T value) ? value : fallback;

        private static void CheckChip<T>(IEnumerable<RadioButton> chips, T value) where T : struct, Enum
        {
            var list = chips.ToList();
            (list.FirstOrDefault(c => c.Tag as string == value.ToString()) ?? list[0]).IsChecked = true;
        }

        private void Format_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            _format = ChipValue(FormatChipList, FileGenFormat.Txt);
            OnOptionChanged();
        }

        private void Option_Click(object sender, RoutedEventArgs e) => OnOptionChanged();

        private void Option_TextChanged(object sender, TextChangedEventArgs e) => OnOptionChanged();

        private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnOptionChanged();

        private void OnOptionChanged()
        {
            if (Ignore) return;
            UpdatePanels();
            SchedulePlan();
            QueueSave();
        }

        private void SizePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag } button) return;
            var parts = tag.Split('|');
            RadioSizeExact.IsChecked = true;
            TxtExactSize.Text = parts[0];
            CmbExactSizeUnit.SelectedIndex = int.Parse(parts[1]);
            UiMotion.Pop(button, 0.9);
            OnOptionChanged();
        }

        private void CountPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag } button) return;
            TxtCount.Text = tag;
            UiMotion.Pop(button, 0.9);
        }

        /// <summary>Shows only what applies to the chosen options.</summary>
        private void UpdatePanels()
        {
            if (!_viewConstructed) return;
            bool exact = RadioSizeExact.IsChecked == true;
            PanelExactSize.Visibility = exact ? Visibility.Visible : Visibility.Collapsed;
            PanelRandomSize.Visibility = exact ? Visibility.Collapsed : Visibility.Visible;
            PanelCustomExtension.Visibility = _format == FileGenFormat.Custom ? Visibility.Visible : Visibility.Collapsed;
            TxtFormatHint.Text = FileGeneratorManager.Describe(_format);

            bool usesFill = FileGeneratorManager.UsesFill(_format);
            FillChips.IsEnabled = usesFill;
            TxtFillNote.Visibility = usesFill && _format is not (FileGenFormat.Csv or FileGenFormat.Json or FileGenFormat.Xml) ? Visibility.Collapsed : Visibility.Visible;
            TxtFillNote.Text = usesFill
                ? "CSV, JSON and XML use the fill only for the text in each value."
                : "PDF, Word, images and MP4 have their own content - the fill isn't used.";
            PanelPatternText.Visibility = usesFill && RadioFillPattern.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            PanelMetadataTag.Visibility = ChkInjectMetadata.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            TxtMetadataHint.Text = _format switch
            {
                FileGenFormat.Pdf => "Goes in the PDF's title and keywords, and on the page.",
                FileGenFormat.Docx => "Goes in the document's properties (title, keywords) and its text.",
                FileGenFormat.Png or FileGenFormat.Jpg => "Goes in the image's comment.",
                FileGenFormat.Json or FileGenFormat.Xml => "Goes in a 'tag' field next to the generation time.",
                FileGenFormat.Txt or FileGenFormat.Csv => "Goes in a first line starting with #.",
                _ => "Raw files have nowhere to keep it - it isn't added.",
            };

            PanelPrefixSuffix.Visibility = RadioNamingPrefixSuffix.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ChkKeepFiles.IsEnabled = RadioArchiveNone.IsChecked != true;
        }

        // =================================================================
        //  Reading the sidebar
        // =================================================================

        /// <summary>The options, or a plain reason they can't be read (a size that isn't a number...).</summary>
        private (FileGenOptions? Options, string? Error) ReadOptions()
        {
            bool exact = RadioSizeExact.IsChecked == true;
            long? exactSize = FileGeneratorManager.ParseSize(TxtExactSize.Text, CmbExactSizeUnit.SelectedIndex);
            long? min = FileGeneratorManager.ParseSize(TxtMinSize.Text, CmbMinSizeUnit.SelectedIndex);
            long? max = FileGeneratorManager.ParseSize(TxtMaxSize.Text, CmbMaxSizeUnit.SelectedIndex);
            string? sizeError = exact
                ? exactSize == null ? "Type a size, like 1 or 2.5." : null
                : min == null || max == null ? "Type both sizes, like 1 and 10." : null;
            ShowSizeError(sizeError);
            if (sizeError != null) return (null, sizeError);
            if (!int.TryParse(TxtCount.Text.Trim().Replace(",", "").Replace(".", ""), out int count) || count < 1) return (null, "Type how many files to make (1 or more).");
            if (!int.TryParse(TxtStartIndex.Text.Trim(), out int start)) return (null, "The start number must be a whole number.");

            return (new FileGenOptions
            {
                Format = _format,
                CustomExtension = TxtCustomExtension.Text,
                SizeMode = exact ? FileGenSizeMode.Exact : FileGenSizeMode.RandomRange,
                ExactSizeBytes = exactSize ?? 0,
                MinSizeBytes = Math.Min(min ?? 0, max ?? 0),
                MaxSizeBytes = Math.Max(min ?? 0, max ?? 0),
                Fill = ChipValue(FillChipList, FileGenFill.RandomBytes),
                PatternText = TxtPatternText.Text,
                InjectMetadata = ChkInjectMetadata.IsChecked == true,
                MetadataTag = TxtMetadataTag.Text,
                Naming = RadioNamingPrefixSuffix.IsChecked == true ? FileGenNaming.PrefixSuffix : FileGenNaming.Sequential,
                BaseName = TxtBaseName.Text,
                Prefix = TxtPrefix.Text,
                Suffix = TxtSuffix.Text,
                StartIndex = start,
                PadNumbers = ChkPadNumbers.IsChecked == true,
                Count = count,
                Archive = ChipValue(ArchiveChipList, FileGenArchive.None),
                KeepFilesAfterArchive = ChkKeepFiles.IsChecked == true,
                OutputFolder = _outputFolder,
            }, null);
        }

        private void ShowSizeError(string? error)
        {
            TxtSizeError.Text = error ?? "";
            TxtSizeError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // =================================================================
        //  Live check: names, total size, free space
        // =================================================================

        private void SchedulePlan(bool immediately = false)
        {
            if (!_loadedOnce) return;
            _checkTimer.Stop();
            if (immediately) _ = UpdatePlanAsync();
            else _checkTimer.Start();
        }

        private async Task UpdatePlanAsync()
        {
            if (_isRunning) return;
            int version = ++_checkVersion;
            var (options, error) = ReadOptions();
            FileGenPlan plan;
            if (options == null) plan = new FileGenPlan { Error = error };
            else
            {
                try
                {
                    plan = await Task.Run(() => FileGeneratorManager.CheckPlan(options));   // probes the disk (can stall on a sleeping network drive)
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    plan = new FileGenPlan { Error = ex.Message };
                }
            }
            if (version != _checkVersion) return;   // superseded by a newer change
            ShowPlan(options, plan);
        }

        private void ShowPlan(FileGenOptions? o, FileGenPlan plan)
        {
            // Names preview (shown even when the folder isn't chosen yet).
            TxtNamePreview.Text = plan.FirstName.Length == 0 ? "–"
                : plan.FirstName == plan.LastName ? plan.FirstName
                : $"{plan.FirstName}  …  {plan.LastName}";

            // Run bar summary.
            if (o != null)
            {
                string each = o.SizeMode == FileGenSizeMode.Exact
                    ? FileGenText.Bytes(o.ExactSizeBytes)
                    : $"{FileGenText.Bytes(o.MinSizeBytes)} – {FileGenText.Bytes(o.MaxSizeBytes)}";
                string kind = o.Format == FileGenFormat.Custom ? FileGeneratorManager.Extension(o).ToUpperInvariant() : o.Format.ToString().ToUpperInvariant();
                TxtPlanSummary.Text = $"{o.Count:N0} × {each} {kind}" + (plan.TotalBytesMax > 0 ? $"  →  up to {FileGenText.Bytes(plan.TotalBytesMax)}" : "")
                                    + (o.Archive != FileGenArchive.None ? $" + {o.Archive.ToString().ToUpperInvariant()}" : "");
            }
            else TxtPlanSummary.Text = "";

            bool wasHidden = PlanCheckPanel.Visibility != Visibility.Visible;
            PlanCheckPanel.Visibility = Visibility.Visible;
            if (!plan.CanRun)
            {
                IconPlanCheck.Kind = PackIconKind.AlertCircleOutline;
                IconPlanCheck.SetResourceReference(ForegroundProperty, "StatusErrorFg");
                TxtPlanCheck.Text = plan.Error;
            }
            else
            {
                IconPlanCheck.Kind = plan.Warning != null ? PackIconKind.InformationOutline : PackIconKind.CheckCircleOutline;
                IconPlanCheck.SetResourceReference(ForegroundProperty, plan.Warning != null ? "StatusWarningFg" : "StatusSuccessFg");
                string space = plan.FreeBytes >= 0
                    ? $"Needs up to {FileGenText.Bytes(plan.TotalBytesMax)} · {FileGenText.Bytes(plan.FreeBytes)} free"
                    : $"Needs up to {FileGenText.Bytes(plan.TotalBytesMax)}";
                TxtPlanCheck.Text = plan.Warning != null ? $"{plan.Warning}\n{space}" : space;
            }
            if (wasHidden) UiMotion.FadeIn(PlanCheckPanel, 4);
            BtnGenerate.IsEnabled = _isRunning || plan.CanRun || _outputFolder.Length == 0;   // without a folder, Generate asks for one
        }

        // =================================================================
        //  Output folder (the shared Output card; session only)
        // =================================================================

        private void OutputCard_FolderChanged(object? sender, EventArgs e) => SchedulePlan(immediately: true);

        // =================================================================
        //  Generate
        // =================================================================

        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Stopping… (the file being written is removed)";
                return;
            }
            if (_outputFolder.Length == 0 || OutputCard.Status.IsError)
            {
                TxtProgress.Text = "Can't save there: " + OutputCard.Status.Text + ". Choose another folder in Output.";
                UiMotion.Pop(OutputCard, 0.95);
                return;
            }

            var (options, error) = ReadOptions();
            FileGenPlan plan = options == null ? new FileGenPlan { Error = error } : await Task.Run(() => FileGeneratorManager.CheckPlan(options));
            ShowPlan(options, plan);
            if (options == null || !plan.CanRun)
            {
                UiMotion.Pop(PlanCheckPanel, 0.95);
                return;
            }

            List<FileGenItem> items;
            try
            {
                items = await Task.Run(() => FileGeneratorManager.PlanItems(options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show("Couldn't prepare the files: " + ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _items.Clear();
            _items.AddRange(items);
            _lastSummary = null;
            _isRunning = true;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            SetRunningUi(true);
            ProgressGenerate.Value = 0;
            TxtProgressPercent.Text = "0%";
            UpdateResultState();

            try
            {
                var progress = new Progress<FileGenProgress>(p =>
                {
                    if (!_isRunning) return;
                    ProgressGenerate.Value = p.Percent;
                    TxtProgressPercent.Text = $"{p.Percent:0}%";
                    TxtProgress.Text = p.FileIndex >= p.FileCount
                        ? (p.CurrentFile.Length > 0 ? p.CurrentFile : "Finishing…")
                        : $"File {p.FileIndex + 1:N0} of {p.FileCount:N0} · {p.CurrentFile}"
                          + (p.FileBytesTotal > 4 * 1024 * 1024 ? $" · {FileGenText.Bytes(p.FileBytesDone)} of {FileGenText.Bytes(p.FileBytesTotal)}" : "");
                });
                var summary = await FileGeneratorManager.GenerateAsync(options, items, progress, _runCts.Token);
                if (_lifetimeCts.IsCancellationRequested) return;
                _lastSummary = summary;
                ProgressGenerate.Value = summary.Cancelled ? ProgressGenerate.Value : 100;
                TxtProgressPercent.Text = $"{ProgressGenerate.Value:0}%";
                TxtProgress.Text = FileGeneratorManager.DescribeSummary(summary);
                if (summary.ArchiveError != null)
                    MessageBox.Show(summary.ArchiveError, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                if (!summary.Cancelled && summary.Elapsed > TimeSpan.FromSeconds(5)) Toast("File generation complete", FileGeneratorManager.DescribeSummary(summary));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TxtProgress.Text = "Couldn't write there: " + FileGeneratorManager.PlainError(ex);
            }
            finally
            {
                _isRunning = false;
                _runCts?.Dispose();
                _runCts = null;
                SetRunningUi(false);
                UpdateResultState();
                SchedulePlan(immediately: true);   // free space moved
            }
        }

        private void SetRunningUi(bool running)
        {
            IconGenerate.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.FilePlusOutline;
            if (running) TxtGenerateLabel.Text = "Cancel";
            else TxtGenerateLabel.SetResourceReference(TextBlock.TextProperty, "Str_FileGen_GenerateButton");
            BtnGenerate.IsEnabled = true;
            UiMotion.Pop(BtnGenerate, 0.96);
            OptionsScrollViewer.IsEnabled = !running;   // the run uses what was set when it started
            OutputCard.IsLocked = running;
        }

        private void UpdateResultState()
        {
            bool any = _items.Count > 0;
            DataGridResults.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (any == (EmptyStatePanel.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(EmptyStatePanel, !any);

            var s = _lastSummary;
            SummaryPanel.Visibility = s != null ? Visibility.Visible : Visibility.Collapsed;
            if (s != null)
            {
                TxtSummaryCount.Text = $"{s.Created:N0} {FileGenText.Plural(s.Created, "file", "files")}";
                TxtTotalSize.Text = FileGenText.Bytes(s.TotalBytes);
                PillFailed.Visibility = s.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
                TxtFailedCount.Text = $"{s.Failed:N0} failed";
                PillArchive.Visibility = s.ArchivePath != null ? Visibility.Visible : Visibility.Collapsed;
                TxtArchive.Text = s.ArchivePath != null ? Path.GetFileName(s.ArchivePath) : "";
                PillArchive.ToolTip = s.ArchivePath;
                UiMotion.Pop(SummaryPill, 0.9);
            }
            BtnExportReport.IsEnabled = !_isRunning && any && s != null;
            BtnOpenFolder.IsEnabled = Directory.Exists(_outputFolder);
            BtnClearResults.IsEnabled = !_isRunning && any;
        }

        // =================================================================
        //  Results: report, open, clear
        // =================================================================

        // The report format is remembered (settings) and ticked in the Export menu.
        private FileGenReportFormat _reportFormat = FileGenReportFormat.Json;

        private void BtnExportReport_Click(object sender, RoutedEventArgs e)
        {
            MenuExportJson.IsChecked = _reportFormat == FileGenReportFormat.Json;
            MenuExportCsv.IsChecked = _reportFormat == FileGenReportFormat.Csv;
            MenuExportXml.IsChecked = _reportFormat == FileGenReportFormat.Xml;
        }

        private async void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count == 0) return;
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out FileGenReportFormat format)) return;
            _reportFormat = format;
            QueueSave();
            string ext = format.ToString().ToLowerInvariant();
            var dialog = new SaveFileDialog
            {
                Title = "Export the report",
                Filter = $"{format.ToString().ToUpperInvariant()} file|*.{ext}",
                DefaultExt = "." + ext,
                FileName = "file-report." + ext,
            };
            if (Directory.Exists(_outputFolder)) dialog.InitialDirectory = _outputFolder;
            if (dialog.ShowDialog() != true) return;
            try
            {
                await FileGeneratorManager.ExportReportAsync(_items.ToList(), dialog.FileName, format);
                TxtProgress.Text = $"Report saved: {Path.GetFileName(dialog.FileName)}.";
                UiMotion.Pop(BtnExportReport, 0.9);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show("Couldn't export: " + FileGeneratorManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(_outputFolder)) Shell(_outputFolder, null);
        }

        private void BtnClearResults_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            _items.Clear();
            _lastSummary = null;
            ProgressGenerate.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.SetResourceReference(TextBlock.TextProperty, "Str_FileGen_ProgressInitial");
            UpdateResultState();
        }

        /// <summary>Double-click a row (not the header or scrollbar): show the file in its folder.</summary>
        private void DataGridResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source is not DataGridRow) source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            if (source is DataGridRow { Item: FileGenItem item } && File.Exists(item.FullPath)) Shell("explorer.exe", $"/select,\"{item.FullPath}\"");
        }

        private static void Shell(string file, string? args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(file) { Arguments = args ?? "", UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                MessageBox.Show("Couldn't open it: " + ex.Message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // =================================================================
        //  Settings (how files are made - never the folder)
        // =================================================================

        private void ApplySettings(FileGenSettings s)
        {
            _applyingSettings = true;
            try
            {
                _format = s.Format;
                CheckChip(FormatChipList, s.Format);
                TxtCustomExtension.Text = s.CustomExtension;
                (s.SizeMode == FileGenSizeMode.Exact ? RadioSizeExact : RadioSizeRandom).IsChecked = true;
                TxtExactSize.Text = s.ExactSize;
                CmbExactSizeUnit.SelectedIndex = s.ExactUnit;
                TxtMinSize.Text = s.MinSize;
                CmbMinSizeUnit.SelectedIndex = s.MinUnit;
                TxtMaxSize.Text = s.MaxSize;
                CmbMaxSizeUnit.SelectedIndex = s.MaxUnit;
                CheckChip(FillChipList, s.Fill);
                TxtPatternText.Text = s.PatternText;
                ChkInjectMetadata.IsChecked = s.InjectMetadata;
                TxtMetadataTag.Text = s.MetadataTag;
                (s.Naming == FileGenNaming.Sequential ? RadioNamingSequential : RadioNamingPrefixSuffix).IsChecked = true;
                TxtBaseName.Text = s.BaseName;
                TxtPrefix.Text = s.Prefix;
                TxtSuffix.Text = s.Suffix;
                TxtStartIndex.Text = s.StartIndex.ToString();
                ChkPadNumbers.IsChecked = s.PadNumbers;
                TxtCount.Text = s.Count.ToString();
                CheckChip(ArchiveChipList, s.Archive);
                ChkKeepFiles.IsChecked = s.KeepFilesAfterArchive;
                _reportFormat = s.ReportFormat;
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            int.TryParse(TxtStartIndex.Text.Trim(), out int start);
            int.TryParse(TxtCount.Text.Trim().Replace(",", ""), out int count);
            FileGeneratorManager.SaveSettings(new FileGenSettings
            {
                Format = _format,
                CustomExtension = TxtCustomExtension.Text,
                SizeMode = RadioSizeExact.IsChecked == true ? FileGenSizeMode.Exact : FileGenSizeMode.RandomRange,
                ExactSize = TxtExactSize.Text,
                ExactUnit = CmbExactSizeUnit.SelectedIndex,
                MinSize = TxtMinSize.Text,
                MinUnit = CmbMinSizeUnit.SelectedIndex,
                MaxSize = TxtMaxSize.Text,
                MaxUnit = CmbMaxSizeUnit.SelectedIndex,
                Fill = ChipValue(FillChipList, FileGenFill.RandomBytes),
                PatternText = TxtPatternText.Text,
                InjectMetadata = ChkInjectMetadata.IsChecked == true,
                MetadataTag = TxtMetadataTag.Text,
                Naming = RadioNamingPrefixSuffix.IsChecked == true ? FileGenNaming.PrefixSuffix : FileGenNaming.Sequential,
                BaseName = TxtBaseName.Text,
                Prefix = TxtPrefix.Text,
                Suffix = TxtSuffix.Text,
                StartIndex = start,
                PadNumbers = ChkPadNumbers.IsChecked == true,
                Count = Math.Max(1, count),
                Archive = ChipValue(ArchiveChipList, FileGenArchive.None),
                KeepFilesAfterArchive = ChkKeepFiles.IsChecked == true,
                ReportFormat = _reportFormat,
            });
        }

        private void QueueSave()
        {
            if (Ignore) return;
            _saveTimer.Stop();
            _saveTimer.Start();
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
