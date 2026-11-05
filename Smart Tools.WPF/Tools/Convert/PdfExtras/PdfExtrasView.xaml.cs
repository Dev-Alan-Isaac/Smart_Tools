using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// PDF Extras - UI only. Reading a PDF and every change is in <see cref="PdfExtrasManager"/>. A list of PDFs (the same
    /// Files card and rows as the PDF Toolkit, and its list helpers) is changed one after the other, and every result is
    /// a new file. The password for protected PDFs lives in this view's field while it is open and is never saved.
    /// </summary>
    public partial class PdfExtrasView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;

        private static readonly int[] TextSizes = { 8, 9, 10, 11, 12, 14, 16, 18, 24 };

        private readonly PdfExtraSettings _settings;
        private readonly ObservableCollection<PdfToolItem> _items = new();
        private readonly ObservableCollection<PdfToolkitGroup> _groups = new();
        private readonly Dictionary<string, PdfExtraInfo> _infos = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _infoGate = new(2);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly DispatcherTimer _passwordTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly bool _viewConstructed;

        private PdfExtraOperation _operation;
        private CancellationTokenSource? _runCts;
        private string _lastOutputFolder = "";
        private int _adding;
        private bool _running;
        private bool _hasRun;
        private bool _applying;
        private bool _loadedOnce;
        private bool _optionsCollapsed;

        public PdfExtrasView()
        {
            InitializeComponent();
            _settings = PdfExtrasManager.LoadSettings();
            OutputCard.DefaultFolder = PdfExtrasManager.DefaultFolder;
            OutputCard.AllowSameAsSource = true;

            ListPages.ItemsSource = _tiles;
            _tiles.CollectionChanged += (_, _) => UpdateEditorState();
            DataGridResults.ItemsSource = _items;
            _items.CollectionChanged += (_, _) => UpdateListState();

            InputCard.ItemsSource = _groups;
            InputCard.FileFilter = PdfToolkitManager.FileDialogFilter;
            _groups.CollectionChanged += (_, _) => InputCard.SetEmpty(_groups.Count == 0);

            _passwordTimer.Tick += (_, _) =>
            {
                _passwordTimer.Stop();
                ReadLockedAgain();
            };
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a run (nothing half-written is kept) and saves the setup.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _lifetimeCts.Cancel();
            _passwordTimer.Stop();
            ReleaseEditor();
            ReadSettings();
            PdfExtrasManager.SaveSettings(_settings);
        }

        private bool IsBusy => _running || _adding > 0;

        private static string T(string key, string fallback = "") => Application.Current?.TryFindResource(key) as string ?? (fallback.Length > 0 ? fallback : key);
        private static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
        private static string Plural(int n, string one, string many) => n == 1 ? T(one) : F(many, n);

        // =================================================================
        //  Setup
        // =================================================================

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;

            OutputCard.SameAsSourceText = T("Str_PdfX_SameAsSource");

            _applying = true;
            try
            {
                foreach (var p in Enum.GetValues<PdfExtraNumberPosition>()) CmbPosition.Items.Add(new ComboBoxItem { Content = T("Str_PdfX_Pos_" + p), Tag = p });
                foreach (var f in Enum.GetValues<PdfExtraNumberFormat>()) CmbFormat.Items.Add(new ComboBoxItem { Content = ExampleFor(f), Tag = f });
                foreach (int size in TextSizes) CmbSize.Items.Add(size);
                foreach (var paper in Enum.GetValues<PdfExtraPaper>()) CmbPaper.Items.Add(new ComboBoxItem { Content = PaperText(paper), Tag = paper });

                SelectTag(CmbPosition, _settings.Position);
                SelectTag(CmbFormat, _settings.Format);
                foreach (var level in Enum.GetValues<PdfBlankSensitivity>()) CmbBlank.Items.Add(new ComboBoxItem { Content = T("Str_PdfX_Blank_" + level), Tag = level });
                SelectTag(CmbPaper, _settings.Paper);
                SelectTag(CmbBlank, _settings.Blank);
                CmbSize.SelectedItem = TextSizes.Contains((int)_settings.FontSize) ? (int)_settings.FontSize : 10;
                TxtStart.Text = _settings.StartNumber.ToString(CultureInfo.CurrentCulture);
                ChkSkipFirst.IsChecked = _settings.SkipFirstPage;

                RbDeg90.IsChecked = _settings.Degrees == 90;
                RbDeg180.IsChecked = _settings.Degrees == 180;
                RbDeg270.IsChecked = _settings.Degrees == 270;
                RbRemove.IsChecked = _settings.PagesMode == PdfExtraPagesMode.Remove;
                RbKeep.IsChecked = _settings.PagesMode == PdfExtraPagesMode.KeepOnly;
                ChkAllowPrint.IsChecked = _settings.AllowPrint;
                ChkAllowCopy.IsChecked = _settings.AllowCopy;
                ChkAllowEdit.IsChecked = _settings.AllowEdit;
                InputCard.IncludeSubfolders = _settings.IncludeSubfolders;
            }
            finally
            {
                _applying = false;
            }

            SetOperation(_settings.Operation);
            UpdateListState();
            ShowStatus(T("Str_PdfX_StatusPick"), "Neutral", PackIconKind.InformationOutline);
        }

        /// <summary>"A4 · 210 × 297 mm".</summary>
        private static string PaperText(PdfExtraPaper paper)
        {
            var (w, h) = PdfExtrasManager.PaperSize(paper);
            return $"{paper} · {w / 72 * 25.4:0} × {h / 72 * 25.4:0} mm";
        }

        private static string ExampleFor(PdfExtraNumberFormat format) =>
            PdfExtrasManager.NumberText(format, 3, 12, T("Str_PdfX_WordPage"), T("Str_PdfX_WordOf"));

        private static void SelectTag<TEnum>(ComboBox box, TEnum value) where TEnum : struct, Enum
        {
            foreach (ComboBoxItem item in box.Items)
                if (item.Tag is TEnum t && EqualityComparer<TEnum>.Default.Equals(t, value)) { box.SelectedItem = item; return; }
            box.SelectedIndex = 0;
        }

        private void ReadSettings()
        {
            if (!_viewConstructed || _applying) return;
            _settings.Operation = _operation;
            _settings.Degrees = RbDeg180.IsChecked == true ? 180 : RbDeg270.IsChecked == true ? 270 : 90;
            _settings.PagesMode = RbKeep.IsChecked == true ? PdfExtraPagesMode.KeepOnly : PdfExtraPagesMode.Remove;
            if (CmbPosition.SelectedItem is ComboBoxItem { Tag: PdfExtraNumberPosition position }) _settings.Position = position;
            if (CmbFormat.SelectedItem is ComboBoxItem { Tag: PdfExtraNumberFormat format }) _settings.Format = format;
            if (CmbSize.SelectedItem is int size) _settings.FontSize = size;
            if (CmbPaper.SelectedItem is ComboBoxItem { Tag: PdfExtraPaper paper }) _settings.Paper = paper;
            if (CmbBlank.SelectedItem is ComboBoxItem { Tag: PdfBlankSensitivity blank }) _settings.Blank = blank;
            _settings.StartNumber = int.TryParse(TxtStart.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int start) ? Math.Clamp(start, 0, 99999) : 1;
            _settings.SkipFirstPage = ChkSkipFirst.IsChecked == true;
            _settings.AllowPrint = ChkAllowPrint.IsChecked == true;
            _settings.AllowCopy = ChkAllowCopy.IsChecked == true;
            _settings.AllowEdit = ChkAllowEdit.IsChecked == true;
            _settings.IncludeSubfolders = InputCard.IncludeSubfolders;
        }

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
        //  Adding PDFs
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!IsBusy) _ = AddPathsAsync(e.Paths);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e) => ClearList();

        /// <summary>Ticking "Include subfolders" also updates the folders already added.</summary>
        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            ReadSettings();
            _ = RescanFoldersAsync(InputCard.IncludeSubfolders);
        }

        private async Task RescanFoldersAsync(bool includeSubfolders)
        {
            var folders = _groups.Where(g => g.IsFolder && g.FolderPath != null && g.IncludeSubfolders != includeSubfolders).ToList();
            if (IsBusy || folders.Count == 0) return;
            _adding++;
            UpdateListState();
            ShowStatus(T("Str_PdfX_StatusLooking"), "Info", PackIconKind.Cached);
            try
            {
                foreach (var group in folders)
                {
                    var scan = await PdfToolkitManager.ScanFolderAsync(group.FolderPath!, includeSubfolders, _lifetimeCts.Token);
                    if (scan.Missing || _running) continue;   // the folder is gone: leave its PDFs alone
                    PdfToolkitManager.ApplyRescan(_items, _groups, group, scan.Files, includeSubfolders);
                }
                ShowStatus(T("Str_PdfX_StatusListUpdated"), "Neutral", PackIconKind.InformationOutline);
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus(F("Str_PdfX_StatusCantAdd", PdfToolkitManager.PlainError(ex)), "Error", PackIconKind.AlertCircleOutline);
            }
            finally
            {
                _adding--;
                UpdateListState();
            }
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok && DropOverlay.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropOverlay, true);
            e.Handled = true;
        }

        /// <summary>DragLeave also fires when moving between child elements: only hide once the pointer is really outside.</summary>
        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddPathsAsync(paths);
            e.Handled = true;
        }

        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (IsBusy || paths.Count == 0) return;
            _adding++;
            UpdateListState();
            ShowStatus(T("Str_PdfX_StatusLooking"), "Info", PackIconKind.Cached);
            try
            {
                bool subfolders = InputCard.IncludeSubfolders;
                var token = _lifetimeCts.Token;
                int found = 0, inaccessible = 0;
                var added = new List<PdfToolItem>();

                foreach (string folder in paths.Where(Directory.Exists))
                {
                    var scan = await PdfToolkitManager.ScanFolderAsync(folder, subfolders, token);
                    if (_running) return;
                    inaccessible += scan.InaccessibleFolders;
                    found += scan.Files.Count;
                    added.AddRange(PdfToolkitManager.AddToList(_items, _groups, scan.Files, folder, isFolder: true, subfolders));
                }
                var (loose, skipped, _) = await PdfToolkitManager.ReadPathsAsync(paths.Where(p => !Directory.Exists(p)), false, token);
                if (_running) return;
                found += loose.Count;
                added.AddRange(PdfToolkitManager.AddToList(_items, _groups, loose, null, isFolder: false, includeSubfolders: false));

                var parts = new List<string>
                {
                    added.Count > 0 ? Plural(added.Count, "Str_PdfX_AddedOne", "Str_PdfX_AddedMany")
                        : found > 0 ? T("Str_PdfX_StatusAlready") : T("Str_PdfX_StatusNoneFound"),
                };
                if (skipped > 0) parts.Add(F("Str_PdfX_StatusSkippedFiles", skipped));
                if (inaccessible > 0) parts.Add(F("Str_PdfX_StatusFoldersFailed", inaccessible));
                ShowStatus(string.Join(" ", parts), added.Count > 0 ? "Success" : "Warning", added.Count > 0 ? PackIconKind.CheckCircleOutline : PackIconKind.AlertOutline);

                if (added.Count > 0)
                {
                    ForgetResult();
                    DataGridResults.ScrollIntoView(added[0]);
                    InputCard.PopDropArea();
                }
            }
            catch (OperationCanceledException)
            {
                // closing
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus(F("Str_PdfX_StatusCantAdd", PdfToolkitManager.PlainError(ex)), "Error", PackIconKind.AlertCircleOutline);
            }
            finally
            {
                _adding--;
                UpdateListState();
            }
        }

        // =================================================================
        //  Reading each PDF (rows on screen only, a couple at a time)
        // =================================================================

        private void DataGridResults_LoadingRow(object? sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item is PdfToolItem { InfoRequested: false } item)
            {
                item.InfoRequested = true;
                _ = LoadInfoAsync(item);
            }
        }

        private async Task LoadInfoAsync(PdfToolItem item)
        {
            var token = _lifetimeCts.Token;
            try
            {
                await _infoGate.WaitAsync(token);
                try
                {
                    string password = PwdShared.Password;
                    var info = await Task.Run(() => PdfExtrasManager.ReadInfo(item.FullPath, password), token);
                    if (token.IsCancellationRequested) return;

                    _infos[item.FullPath] = info;
                    if (info.NeedsPassword || info.Problem == "WrongPassword") SetSharedOpen(true);   // the card is closed until a PDF actually needs it
                    item.PageCount = info.PageCount > 0 ? info.PageCount : -1;
                    if (item.Status == PdfItemStatus.Ready)
                    {
                        string note = LockNote(info);
                        item.Message = info.Problem.Length > 0 && note.Length == 0 ? PdfToolkitManager.PlainError(new InvalidDataException(info.Problem)) : note;
                        item.Result = note;
                    }
                    UpdateAll();
                }
                finally
                {
                    _infoGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                item.InfoRequested = false;
            }
        }

        /// <summary>What is in the way of changing a PDF, in a few words; empty when nothing is.</summary>
        private static string LockNote(PdfExtraInfo info) =>
            info.NeedsPassword ? T("Str_PdfX_RowNeedsPassword")
            : info.Problem == "WrongPassword" ? T("Str_PdfX_RowWrongPassword")
            : info.NeedsOwnerPassword ? T("Str_PdfX_RowNeedsOwner")
            : "";

        private void SharedHeader_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            SetSharedOpen(PanelShared.Visibility != Visibility.Visible);

        private void SetSharedOpen(bool open)
        {
            PanelShared.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            IconSharedChevron.Kind = open ? MaterialDesignThemes.Wpf.PackIconKind.ChevronUp : MaterialDesignThemes.Wpf.PackIconKind.ChevronDown;
            if (open) Dispatcher.InvokeAsync(() => PwdShared.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void PwdShared_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            _passwordTimer.Stop();
            _passwordTimer.Start();
        }

        /// <summary>The password changed: read again the PDFs that were waiting for one.</summary>
        private void ReadLockedAgain()
        {
            if (Arranging && _tiles.Count == 0) _ = LoadEditorForSelectionAsync(force: true);
            foreach (var item in _items.Where(i => i.Status == PdfItemStatus.Ready && _infos.TryGetValue(i.FullPath, out var info) && LockNote(info).Length > 0).ToList())
            {
                item.InfoRequested = true;
                _ = LoadInfoAsync(item);
            }
        }

        // =================================================================
        //  The list: tick, remove, clear
        // =================================================================

        private List<PdfToolItem> Ticked() => _items.Where(i => i.IsIncluded).ToList();

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            ForgetResult();
            UpdateListState();
            UpdateAll();
        }

        private void ChkIncludeAll_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy) return;
            bool include = ChkIncludeAll.IsChecked == true;
            foreach (var item in _items) item.IsIncluded = include;
            ForgetResult();
            UpdateListState();
            UpdateAll();
        }

        private void DataGridResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateListState();
            if (Arranging) _ = LoadEditorForSelectionAsync();
        }

        private void DataGridResults_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete || IsBusy) return;
            RemoveItems(DataGridResults.SelectedItems.OfType<PdfToolItem>().ToList());
            e.Handled = true;
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBusy) RemoveItems(DataGridResults.SelectedItems.OfType<PdfToolItem>().ToList());
        }

        private void BtnRowRemove_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBusy && sender is Button { Tag: PdfToolItem item }) RemoveItems(new List<PdfToolItem> { item });
        }

        private void BtnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (!IsBusy && sender is Button { Tag: PdfToolkitGroup group }) RemoveItems(group.Items.ToList());
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e) => ClearList();

        private void ClearList()
        {
            if (IsBusy || _items.Count == 0) return;
            _items.Clear();
            _groups.Clear();
            _infos.Clear();
            if (Arranging) _ = LoadEditorForSelectionAsync(force: true);
            ForgetResult();
            ShowStatus(T("Str_PdfX_StatusPick"), "Neutral", PackIconKind.InformationOutline);
            UpdateAll();
        }

        private void RemoveItems(List<PdfToolItem> items)
        {
            if (items.Count == 0) return;
            PdfToolkitManager.RemoveFromList(_items, _groups, items);
            foreach (var item in items) _infos.Remove(item.FullPath);
            ForgetResult();
            UpdateAll();
            if (Arranging && _editorItem != null && !_items.Contains(_editorItem)) _ = LoadEditorForSelectionAsync(force: true);
        }

        /// <summary>The counts above the list, the empty state, and which list buttons work.</summary>
        private void UpdateListState()
        {
            if (!_viewConstructed) return;
            int total = _items.Count, ticked = _items.Count(i => i.IsIncluded);
            SummaryPanel.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStatePanel.Visibility = total > 0 ? Visibility.Collapsed : Visibility.Visible;
            DataGridResults.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyRowHeights();
            TxtSummaryCount.Text = ticked == total ? Plural(total, "Str_PdfX_CountOne", "Str_PdfX_CountMany") : F("Str_PdfX_CountTicked", ticked, total);

            ChkIncludeAll.IsChecked = total == 0 || ticked == total ? true : ticked == 0 ? false : null;

            SetPill(PillDone, TxtSummaryDone, _items.Count(i => i.Status == PdfItemStatus.Done), "Str_PdfX_PillDone");
            SetPill(PillSkipped, TxtSummarySkipped, _items.Count(i => i.Status == PdfItemStatus.Skipped), "Str_PdfX_PillSkipped");
            SetPill(PillFailed, TxtSummaryFailed, _items.Count(i => i.Status == PdfItemStatus.Failed), "Str_PdfX_PillFailed");

            bool idle = !IsBusy;
            BtnClearAll.IsEnabled = idle && total > 0;
            BtnRemoveSelected.IsEnabled = idle && DataGridResults.SelectedItems.Count > 0;
            BtnApply.IsEnabled = _running || (idle && CanApply());

            static void SetPill(Border pill, TextBlock text, int count, string key)
            {
                pill.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                text.Text = F(key, count);
            }
        }

        // =================================================================
        //  Operation and options
        // =================================================================

        private void Operation_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applying || !_viewConstructed) return;
            if (CmbOperation.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<PdfExtraOperation>(tag, out var op)) SetOperation(op);
        }

        private void SetOperation(PdfExtraOperation op)
        {
            if (op != _operation) ForgetResult();
            _operation = op;
            _applying = true;
            try
            {
                CmbOperation.SelectedItem = CmbOperation.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is string tag && tag == op.ToString());
            }
            finally
            {
                _applying = false;
            }

            PanelRotate.Visibility = Show(op == PdfExtraOperation.Rotate);
            PanelPages.Visibility = Show(op == PdfExtraOperation.Pages);
            PanelNumbers.Visibility = Show(op == PdfExtraOperation.PageNumbers);
            PanelProperties.Visibility = Show(op == PdfExtraOperation.Properties);
            PanelProtect.Visibility = Show(op == PdfExtraOperation.Protect);
            PanelUnlock.Visibility = Show(op == PdfExtraOperation.Unlock);
            PanelCrop.Visibility = Show(op == PdfExtraOperation.Crop);
            PanelSize.Visibility = Show(op == PdfExtraOperation.PageSize);
            PanelScope.Visibility = Show(op is PdfExtraOperation.Rotate or PdfExtraOperation.Crop or PdfExtraOperation.PageSize);
            PanelArrange.Visibility = Show(op == PdfExtraOperation.Arrange);
            TxtOperationHint.Text = T("Str_PdfX_Hint_" + op);

            ReadSettings();
            ShowArrangeMode(op == PdfExtraOperation.Arrange);
            UpdateAll();

            static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Setting_Click(object sender, RoutedEventArgs e) => OnSettingChanged();
        private void Setting_TextChanged(object sender, TextChangedEventArgs e) => OnSettingChanged();
        private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnSettingChanged();
        private void Setting_PasswordChanged(object sender, RoutedEventArgs e) => OnSettingChanged();

        private void OnSettingChanged()
        {
            if (!_viewConstructed || _applying) return;
            ReadSettings();
            ForgetResult();
            UpdateAll();
        }

        /// <summary>A result belongs to the settings it was made with: changing them starts a new run, so the rows and the status go back to ready.</summary>
        private void ForgetResult()
        {
            if (!_hasRun || _running) return;
            _hasRun = false;
            foreach (var item in _items)
            {
                if (item.Status == PdfItemStatus.Ready) continue;
                item.Status = PdfItemStatus.Ready;
                item.Message = "";
                item.Result = _infos.TryGetValue(item.FullPath, out var info) ? LockNote(info) : "";
                item.Outputs.Clear();
            }
            BtnOpenOutput.Visibility = Visibility.Collapsed;
            if (_items.Count > 0) ShowStatus(T("Str_PdfX_StatusReady"), "Neutral", PackIconKind.InformationOutline);
            UpdateListState();
        }

        // =================================================================
        //  What Apply will do
        // =================================================================

        private void UpdateAll()
        {
            if (!_viewConstructed) return;
            UpdateNotes();
            bool clean = ChkCleanAll.IsChecked == true;
            TxtTitle.IsEnabled = TxtAuthor.IsEnabled = TxtSubject.IsEnabled = TxtKeywords.IsEnabled = !clean;
            TxtPlan.Text = PlanText();
            BtnApply.IsEnabled = _running || (!IsBusy && CanApply());
        }

        /// <summary>What is known about the ticked PDFs that were read.</summary>
        private List<PdfExtraInfo> TickedInfos() => Ticked().Select(i => _infos.TryGetValue(i.FullPath, out var info) ? info : null).OfType<PdfExtraInfo>().ToList();

        /// <summary>The page count when exactly one PDF is ticked and read; otherwise 0.</summary>
        private int SinglePageCount => Ticked() is { Count: 1 } one && _infos.TryGetValue(one[0].FullPath, out var info) && info.PageCount > 0 ? info.PageCount : 0;

        /// <summary>The page boxes explain themselves as you type: how many ranges, or what is wrong.</summary>
        private void UpdateNotes()
        {
            TxtRotatePagesNote.Text = PagesNote(TxtRotatePages.Text, blankMeansAll: true);
            TxtPagesNote.Text = PagesNote(TxtPages.Text, blankMeansAll: false);
            TxtCropNote.Text = CropNote();

            if (CmbFormat.SelectedItem is ComboBoxItem { Tag: PdfExtraNumberFormat format })
            {
                int start = _settings.StartNumber;
                int pages = SinglePageCount > 0 ? SinglePageCount : 10;
                TxtNumbersPreview.Text = F("Str_PdfX_NumbersPreview", PdfExtrasManager.NumberText(format, start, Math.Max(start, pages + start - 1 - (_settings.SkipFirstPage ? 1 : 0)), T("Str_PdfX_WordPage"), T("Str_PdfX_WordOf")));
            }
        }

        /// <summary>Margins typed in the Crop card, in millimeters. False when a box is not a number or is negative; blank counts as 0.</summary>
        private bool TryReadMargins(out double top, out double right, out double bottom, out double left)
        {
            bool Read(string text, out double value)
            {
                text = text.Trim();
                if (text.Length == 0) { value = 0; return true; }
                bool ok = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                return ok && value >= 0 && value < 10000;
            }
            top = right = bottom = left = 0;
            return Read(TxtCropTop.Text, out top) & Read(TxtCropRight.Text, out right) & Read(TxtCropBottom.Text, out bottom) & Read(TxtCropLeft.Text, out left);
        }

        /// <summary>What the margins leave of the smallest ticked page, or what is wrong with them.</summary>
        private string CropNote()
        {
            if (!TryReadMargins(out double t, out double r, out double b, out double l)) return T("Str_PdfX_CropBadNumber");
            if (t + r + b + l <= 0) return T("Str_PdfX_CropNone");

            var sizes = TickedInfos().Where(i => i.PageWidth > 0).ToList();
            if (sizes.Count == 0) return T("Str_PdfX_CropNone");

            // The first page of the smallest ticked PDF is the one most likely to be left with nothing.
            var smallest = sizes.OrderBy(i => i.PageWidth * i.PageHeight).First();
            double w = smallest.PageWidth / 72 * 25.4 - l - r, h = smallest.PageHeight / 72 * 25.4 - t - b;
            return w * 72 / 25.4 < 20 || h * 72 / 25.4 < 20
                ? T("Str_PdfX_CropTooBig")
                : F("Str_PdfX_CropLeaves", w.ToString("0.#", CultureInfo.CurrentCulture), h.ToString("0.#", CultureInfo.CurrentCulture));
        }

        private string PagesNote(string text, bool blankMeansAll)
        {
            if (string.IsNullOrWhiteSpace(text)) return blankMeansAll ? T("Str_PdfX_AllPagesNote") : T("Str_PdfX_PagesExample");
            var parse = PdfToolkitManager.ParseRanges(text);
            if (!parse.IsValid) return PdfToolkitManager.DescribeRanges(parse);

            int count = SinglePageCount;
            if (count == 0) return PdfToolkitManager.DescribeRanges(parse) + " " + T("Str_PdfX_BatchPagesNote");

            var pages = PdfExtrasManager.PagesFromText(text, count, blankMeansAll, out var outside);
            if (pages == null) return F("Str_PdfX_NoSuchPages", count);
            return outside.Count > 0
                ? F("Str_PdfX_PagesSomeOutside", pages.Count, string.Join(", ", outside), count)
                : pages.Count == 1 ? T("Str_PdfX_OnePageNote") : F("Str_PdfX_PagesNote", pages.Count);
        }

        /// <summary>The shared "which pages" box: empty means every page, otherwise it has to be a valid list.</summary>
        private bool ScopeValid() => string.IsNullOrWhiteSpace(TxtRotatePages.Text) || PdfToolkitManager.ParseRanges(TxtRotatePages.Text).IsValid;

        /// <summary>"every page" or "pages 1-3, 5".</summary>
        private string ScopeText()
        {
            if (string.IsNullOrWhiteSpace(TxtRotatePages.Text)) return SinglePageCount > 0 ? F("Str_PdfX_ScopeAll", SinglePageCount) : T("Str_PdfX_ScopeEvery");
            var parse = PdfToolkitManager.ParseRanges(TxtRotatePages.Text);
            return parse.IsValid ? F("Str_PdfX_ScopePages", string.Join(", ", parse.Ranges)) : "";
        }

        private bool CanApply()
        {
            if (Arranging) return _editorItem != null && EditorChanged();
            if (Ticked().Count == 0) return false;
            switch (_operation)
            {
                case PdfExtraOperation.Rotate:
                case PdfExtraOperation.PageSize:
                    return ScopeValid();
                case PdfExtraOperation.Pages:
                    return PdfToolkitManager.ParseRanges(TxtPages.Text).IsValid;
                case PdfExtraOperation.Crop:
                    return ScopeValid() && TryReadMargins(out double t, out double r, out double b, out double l) && t + r + b + l > 0 && CropNote() != T("Str_PdfX_CropTooBig");
                case PdfExtraOperation.Protect:
                    return PwdUser.Password.Length > 0 || ChkAllowPrint.IsChecked != true || ChkAllowCopy.IsChecked != true || ChkAllowEdit.IsChecked != true;
                default:
                    return true;
            }
        }

        /// <summary>One or two sentences saying what will happen, to how many PDFs, and what the new files are called.</summary>
        private string PlanText()
        {
            if (Arranging) return ArrangePlan();
            var ticked = Ticked();
            if (ticked.Count == 0) return T(_items.Count == 0 ? "Str_PdfX_PlanNoFiles" : "Str_PdfX_PlanNoneTicked");

            string what = _operation switch
            {
                PdfExtraOperation.Rotate => RotatePlan(),
                PdfExtraOperation.Pages => PagesPlan(),
                PdfExtraOperation.PageNumbers => NumbersPlan(),
                PdfExtraOperation.Properties => ChkCleanAll.IsChecked == true ? T("Str_PdfX_PlanClean") : T("Str_PdfX_PlanProperties"),
                PdfExtraOperation.Protect => ProtectPlan(),
                PdfExtraOperation.Crop => CropPlan(),
                PdfExtraOperation.PageSize => ScopeValid() ? F("Str_PdfX_PlanSize", ScopeText(), _settings.Paper) : T("Str_PdfX_PlanCheckPages"),
                _ => TickedInfos().Any(i => i.IsEncrypted) ? T("Str_PdfX_PlanUnlock") : T("Str_PdfX_PlanNotProtected"),
            };

            bool anyProtected = TickedInfos().Any(i => i.IsEncrypted);
            string drops = anyProtected && _operation is not (PdfExtraOperation.Protect or PdfExtraOperation.Unlock) ? " " + T("Str_PdfX_PlanDropsProtection") : "";
            string name = PdfExtrasManager.OutputFileName(_operation, _settings.PagesMode, ticked[0].FullPath);
            string saved = ticked.Count == 1 ? F("Str_PdfX_PlanSaved", name) : F("Str_PdfX_PlanSavedBatch", ticked.Count, name);
            return what + drops + " " + saved;
        }

        private string CropPlan()
        {
            if (!ScopeValid()) return T("Str_PdfX_PlanCheckPages");
            if (!TryReadMargins(out double t, out double r, out double b, out double l) || t + r + b + l <= 0) return T("Str_PdfX_PlanCropNone");
            string F1(double v) => v.ToString("0.#", CultureInfo.CurrentCulture);
            return F("Str_PdfX_PlanCrop", ScopeText(), F1(t), F1(r), F1(b), F1(l));
        }

        private string RotatePlan()
        {
            if (!ScopeValid()) return T("Str_PdfX_PlanCheckPages");
            string turn = _settings.Degrees switch { 180 => T("Str_PdfX_Turn180"), 270 => T("Str_PdfX_TurnLeft"), _ => T("Str_PdfX_TurnRight") };
            return string.IsNullOrWhiteSpace(TxtRotatePages.Text) && SinglePageCount > 0
                ? F("Str_PdfX_PlanRotateAll", SinglePageCount, turn)
                : F("Str_PdfX_PlanRotate", ScopeText(), turn);
        }

        private string PagesPlan()
        {
            var parse = PdfToolkitManager.ParseRanges(TxtPages.Text);
            if (!parse.IsValid) return T("Str_PdfX_PlanCheckPages");
            string list = string.Join(", ", parse.Ranges);

            int count = SinglePageCount;
            var pages = count > 0 ? PdfExtrasManager.PagesFromText(TxtPages.Text, count, false, out _) : null;
            if (pages == null)
                return _settings.PagesMode == PdfExtraPagesMode.Remove ? F("Str_PdfX_PlanRemoveBatch", list) : F("Str_PdfX_PlanKeepBatch", list);
            return _settings.PagesMode == PdfExtraPagesMode.Remove
                ? F("Str_PdfX_PlanRemove", list, Math.Max(0, count - pages.Count))
                : F("Str_PdfX_PlanKeep", list, pages.Count);
        }

        private string NumbersPlan()
        {
            string example = ExampleFor(_settings.Format);
            string where = T("Str_PdfX_Pos_" + _settings.Position).ToLowerInvariant();
            return F(_settings.SkipFirstPage ? "Str_PdfX_PlanNumbersSkip" : "Str_PdfX_PlanNumbers", example, where);
        }

        private string ProtectPlan()
        {
            var parts = new List<string>();
            if (PwdUser.Password.Length > 0) parts.Add(T("Str_PdfX_PlanNeedsPassword"));
            var blocked = new List<string>();
            if (ChkAllowPrint.IsChecked != true) blocked.Add(T("Str_PdfX_NoPrinting"));
            if (ChkAllowCopy.IsChecked != true) blocked.Add(T("Str_PdfX_NoCopying"));
            if (ChkAllowEdit.IsChecked != true) blocked.Add(T("Str_PdfX_NoEditing"));
            if (blocked.Count > 0) parts.Add(F("Str_PdfX_PlanBlocks", string.Join(", ", blocked)));
            return parts.Count == 0 ? T("Str_PdfX_PlanProtectNothing") : string.Join(" ", parts);
        }

        // =================================================================
        //  Applying
        // =================================================================

        private static double MarginMm(string text) =>
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double v) || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? Math.Max(0, v) : 0;

        private PdfExtraRequest BuildRequest(PdfToolItem item, string folder) => new()
        {
            Operation = _operation,
            SourcePath = item.FullPath,
            OutputFolder = folder,
            OpenPassword = PwdShared.Password,
            Degrees = _settings.Degrees,
            Pages = _operation == PdfExtraOperation.Pages ? TxtPages.Text : TxtRotatePages.Text,
            Paper = _settings.Paper,
            CropTop = MarginMm(TxtCropTop.Text),
            CropRight = MarginMm(TxtCropRight.Text),
            CropBottom = MarginMm(TxtCropBottom.Text),
            CropLeft = MarginMm(TxtCropLeft.Text),
            PagesMode = _settings.PagesMode,
            Position = _settings.Position,
            Format = _settings.Format,
            StartNumber = _settings.StartNumber,
            FontSize = _settings.FontSize,
            SkipFirstPage = _settings.SkipFirstPage,
            PageWord = T("Str_PdfX_WordPage"),
            OfWord = T("Str_PdfX_WordOf"),
            CleanAll = ChkCleanAll.IsChecked == true,
            Title = TxtTitle.Text,
            Author = TxtAuthor.Text,
            Subject = TxtSubject.Text,
            Keywords = TxtKeywords.Text,
            NewUserPassword = PwdUser.Password,
            NewOwnerPassword = PwdOwner.Password,
            AllowPrint = ChkAllowPrint.IsChecked == true,
            AllowCopy = ChkAllowCopy.IsChecked == true,
            AllowEdit = ChkAllowEdit.IsChecked == true,
        };

        private async void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
            {
                _runCts?.Cancel();
                return;
            }
            var ticked = Ticked();
            if ((!Arranging && ticked.Count == 0) || !CanApply() || IsBusy) return;

            // Where to save: next to each original, or the folder on the Save to card.
            string? sharedFolder = null;
            if (!OutputCard.IsSameAsSource)
            {
                if (OutputCard.Status.IsError)
                {
                    ShowStatus(F("Str_PdfX_StatusCantSaveThere", OutputCard.Status.Text), "Error", PackIconKind.AlertCircleOutline);
                    return;
                }
                try
                {
                    sharedFolder = OutputCard.EnsureFolder() ?? PdfExtrasManager.DefaultFolder;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    ShowStatus(F("Str_PdfX_StatusFolderFailed", PdfToolkitManager.PlainError(ex)), "Error", PackIconKind.AlertCircleOutline);
                    return;
                }
            }

            if (Arranging)
            {
                await ApplyArrangeAsync(sharedFolder);
                return;
            }

            var cts = _runCts = new CancellationTokenSource();
            var token = cts.Token;
            _hasRun = true;
            foreach (var item in _items)
            {
                item.Status = PdfItemStatus.Ready;
                item.Message = "";
                item.Result = "";
                item.Outputs.Clear();
            }
            BtnOpenOutput.Visibility = Visibility.Collapsed;
            SetRunning(true);

            int done = 0, skipped = 0, failed = 0, cancelled = 0;
            _lastOutputFolder = "";
            for (int i = 0; i < ticked.Count; i++)
            {
                var item = ticked[i];
                if (token.IsCancellationRequested)
                {
                    item.Status = PdfItemStatus.Cancelled;
                    cancelled++;
                    continue;
                }

                item.Status = PdfItemStatus.Working;
                ShowStatus(F("Str_PdfX_StatusWorkingOn", i + 1, ticked.Count), "Info", PackIconKind.Cached);
                ProgressRun.Value = i * 100.0 / ticked.Count;
                DataGridResults.ScrollIntoView(item);

                string folder = sharedFolder ?? Path.GetDirectoryName(item.FullPath) ?? PdfExtrasManager.DefaultFolder;
                var request = BuildRequest(item, folder);
                var result = await Task.Run(() => PdfExtrasManager.Apply(request, null, token));

                switch (RecordResult(item, result))
                {
                    case PdfItemStatus.Done: done++; _lastOutputFolder = Path.GetDirectoryName(result.OutputPath) ?? _lastOutputFolder; break;
                    case PdfItemStatus.Skipped: skipped++; break;
                    case PdfItemStatus.Cancelled: cancelled++; break;
                    default: failed++; break;
                }
                UpdateListState();
            }

            ProgressRun.Value = 100;
            SetRunning(false);
            ShowRunSummary(done, skipped, failed, cancelled);
        }

        private void SetRunning(bool running)
        {
            _running = running;
            ProgressRun.Value = 0;
            ProgressRun.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            IconApply.Kind = running ? PackIconKind.StopCircleOutline : PackIconKind.PlayCircleOutline;
            TxtApply.Text = running ? T("Str_PdfX_Cancel") : T("Str_PdfX_Apply");
            OutputCard.IsLocked = running;
            InputCard.IsLocked = running;
            if (!running) OutputCard.Refresh();

            CmbOperation.IsEnabled = !running;
            EditorToolbar.IsEnabled = !running && _tiles.Count > 0;
            ListPages.IsEnabled = !running;
            UpdateListState();
        }

        /// <summary>Puts one PDF's outcome on its row and says which kind of row it became.</summary>
        private PdfItemStatus RecordResult(PdfToolItem item, PdfExtraResult result)
        {
            switch (result.Outcome)
            {
                case PdfExtraOutcome.Done:
                    item.Status = PdfItemStatus.Done;
                    item.Outputs.Add(result.OutputPath);
                    item.Result = RowResult(result);
                    item.Message = F("Str_PdfX_StatusSaved", Path.GetFileName(result.OutputPath)) + " - " + result.OutputPath
                        + (result.ProtectionDropped ? "\n" + T("Str_PdfX_DoneUnprotected") : "")
                        + (result.OutsideRanges.Count > 0 ? "\n" + F("Str_PdfX_SomeOutside", string.Join(", ", result.OutsideRanges)) : "");
                    return PdfItemStatus.Done;
                case PdfExtraOutcome.Canceled:
                    item.Status = PdfItemStatus.Cancelled;
                    return PdfItemStatus.Cancelled;
                case PdfExtraOutcome.Failed:
                    item.Status = PdfItemStatus.Failed;
                    item.Message = item.Result = F("Str_PdfX_StatusFailed", result.Detail);
                    return PdfItemStatus.Failed;
                default:
                    item.Status = PdfItemStatus.Skipped;
                    item.Message = item.Result = SkipReason(result);
                    return PdfItemStatus.Skipped;
            }
        }

        /// <summary>Why a PDF was left alone, in a few words.</summary>
        private string SkipReason(PdfExtraResult r) => r.Outcome switch
        {
            PdfExtraOutcome.NothingToDo => T(_operation == PdfExtraOperation.Properties && ChkCleanAll.IsChecked == true ? "Str_PdfX_Nothing_Clean" : "Str_PdfX_Nothing_" + _operation),
            PdfExtraOutcome.BadPages => r.OutsideRanges.Count > 0 ? F("Str_PdfX_RowPagesOutside", string.Join(", ", r.OutsideRanges)) : T("Str_PdfX_RowBadPages"),
            PdfExtraOutcome.NoPageLeft => T("Str_PdfX_StatusNoPageLeft"),
            PdfExtraOutcome.CropTooBig => F("Str_PdfX_StatusCropTooBig", r.Detail),
            PdfExtraOutcome.NeedsPassword => T("Str_PdfX_RowNeedsPassword"),
            PdfExtraOutcome.WrongPassword => T("Str_PdfX_RowWrongPassword"),
            PdfExtraOutcome.NeedsOwnerPassword => T("Str_PdfX_RowNeedsOwner"),
            _ => "",
        };

        /// <summary>"6 pages · 30.7 KB → 44.9 KB" for the Result column.</summary>
        private string RowResult(PdfExtraResult r)
        {
            string sizes = $"{PdfToolText.FormatBytes(r.SizeBefore)} → {PdfToolText.FormatBytes(r.SizeAfter)}";
            string pages = _operation switch
            {
                PdfExtraOperation.Pages => F("Str_PdfX_RowPagesLeft", r.PagesAfter),
                PdfExtraOperation.Rotate or PdfExtraOperation.PageNumbers or PdfExtraOperation.Crop or PdfExtraOperation.PageSize => Plural(r.PagesAffected, "Str_PdfX_OnePage", "Str_PdfX_Pages_N"),
                PdfExtraOperation.Arrange => Plural(r.PagesAfter, "Str_PdfX_OnePage", "Str_PdfX_Pages_N"),
                _ => "",
            };
            string dropped = r.ProtectionDropped ? " · " + T("Str_PdfX_RowUnprotected") : "";
            return (pages.Length > 0 ? pages + " · " : "") + sizes + dropped;
        }

        private void ShowRunSummary(int done, int skipped, int failed, int cancelled)
        {
            var parts = new List<string>();
            if (done > 0) parts.Add(F("Str_PdfX_PillDone", done));
            if (skipped > 0) parts.Add(F("Str_PdfX_PillSkipped", skipped));
            if (failed > 0) parts.Add(F("Str_PdfX_PillFailed", failed));
            if (cancelled > 0) parts.Add(F("Str_PdfX_PillCanceled", cancelled));

            string text = parts.Count > 0 ? string.Join(" · ", parts) : T("Str_PdfX_StatusCanceled");
            if (cancelled > 0 && done == 0) text = T("Str_PdfX_StatusCanceled");

            if (failed > 0) ShowStatus(text, "Error", PackIconKind.AlertCircleOutline);
            else if (done > 0 && skipped == 0 && cancelled == 0) ShowStatus(text, "Success", PackIconKind.CheckCircleOutline);
            else if (done > 0) ShowStatus(text, "Warning", PackIconKind.AlertOutline);
            else ShowStatus(text, cancelled > 0 ? "Neutral" : "Warning", cancelled > 0 ? PackIconKind.InformationOutline : PackIconKind.AlertOutline);

            BtnOpenOutput.Visibility = done > 0 && _lastOutputFolder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (done > 0) UiMotion.Pop(BorderStatus, 0.95);
            UpdateListState();
        }

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOutputFolder.Length == 0 || !Directory.Exists(_lastOutputFolder)) return;
            try
            {
                Process.Start(new ProcessStartInfo(_lastOutputFolder) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowStatus(F("Str_PdfX_StatusFailed", ex.Message), "Error", PackIconKind.AlertCircleOutline);
            }
        }

        private void ShowStatus(string text, string kind, PackIconKind icon)
        {
            TxtStatus.Text = text;
            TxtStatus.ToolTip = text;
            IconStatus.Kind = icon;

            (string background, string foreground) = kind == "Neutral" ? ("RowBg", "TextSecondary") : ($"Status{kind}Bg", $"Status{kind}Fg");
            BorderStatus.SetResourceReference(Border.BackgroundProperty, background);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, foreground);
            IconStatus.SetResourceReference(PackIcon.ForegroundProperty, foreground);
        }
    }
}
