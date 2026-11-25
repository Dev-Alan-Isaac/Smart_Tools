using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Archive Manager - UI only. 7-Zip runs in <see cref="ArchiveManager"/>; split-set detection, command lines and the reading of
    /// 7-Zip's listing are the pure code in Core\ (tested).
    /// </summary>
    public partial class ArchiveManagerView : UserControl
    {
        private static string DialogTitle => Loc.T("Str_Tool_archive-manager_Name", "Archive Manager");

        private readonly ArchiveSettings _settings = ArchiveManager.LoadSettings();
        private readonly ObservableCollection<FolderListEntry> _openList = new();
        private readonly ObservableCollection<FolderListEntry> _createList = new();
        private readonly ObservableCollection<ArchiveRow> _rows = new();
        private CancellationTokenSource? _cts;
        private bool _busy;
        private bool _loadedOnce;
        private bool _loading;                  // controls are being filled: their events are not the user's
        private string _archive = "";           // the file shown (any part of a split archive)
        private SplitSet? _split;
        private ArchiveListing? _listing;
        private string _resultPath = "";        // what "show in folder" opens: the extracted folder or the new archive

        public ArchiveManagerView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _openList;
            CreateInput.ItemsSource = _createList;
            GridEntries.ItemsSource = _rows;
            _openList.CollectionChanged += (_, _) => InputCard.SetEmpty(_openList.Count == 0);
            _createList.CollectionChanged += (_, _) => CreateInput.SetEmpty(_createList.Count == 0);
            InputCard.FileFilter = Loc.T("Str_Archive_OpenFilter", "Archives") + "|*.7z;*.zip;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.zst;*.cab;*.iso;*.wim;*.001;*.z01;*.r00|" + Loc.T("Str_Archive_AllFiles", "All files") + "|*.*";
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _loading = true;
            RbCreate.IsChecked = _settings.Creating;
            RbExtract.IsChecked = !_settings.Creating;
            Select(CmbFormat, _settings.Format.ToString());
            Select(CmbLevel, _settings.Level.ToString());
            ChkSplit.IsChecked = _settings.Split;
            TxtSplit.Text = _settings.SplitSize;
            ChkHideNames.IsChecked = _settings.HideNames;
            _loading = false;
            ApplyMode();
            if (!ArchiveManager.IsAvailable)
                ShowStatus(Loc.T("Str_Archive_NoEngine", "The 7-Zip program that Archive Manager uses is missing from this copy of Smart Tools (Engines\\7ZIP)."), StatusKind.Error);
        }

        private static void Select(ComboBox box, string tag)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
                if (item.Tag as string == tag)
                {
                    box.SelectedItem = item;
                    return;
                }
            box.SelectedIndex = 0;
        }

        private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

        private bool Creating => RbCreate.IsChecked == true;

        // =================================================================
        //  Mode and options
        // =================================================================

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce || _loading || Creating == _settings.Creating) return;   // building the page, or nothing changed
            if (_busy)
            {
                _loading = true;
                RbCreate.IsChecked = _settings.Creating;   // not while something runs
                RbExtract.IsChecked = !_settings.Creating;
                _loading = false;
                return;
            }
            _settings.Creating = Creating;
            ArchiveManager.SaveSettings(_settings);
            ApplyMode();
        }

        private void ApplyMode()
        {
            bool create = Creating;
            PanelExtract.Visibility = create ? Visibility.Collapsed : Visibility.Visible;
            PanelCreate.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
            OutputCard.Header = create ? Loc.T("Str_Archive_SaveTo", "Save the archive to") : Loc.T("Str_Archive_ExtractTo", "Extract to");
            UpdateRunButton();
            UpdateOptionState();
            HideStatus();
            ShowList(null);
            if (!create && _archive.Length > 0 && _listing == null) _ = LoadListingAsync();
            UpdateEmpty();
        }

        private void UpdateRunButton()
        {
            bool create = Creating;
            TxtStart.Text = _busy ? Loc.T("Str_Archive_Stop", "Stop") : create ? Loc.T("Str_Archive_CreateButton", "Create archive") : Loc.T("Str_Archive_ExtractAll", "Extract all");
            IconStart.Kind = _busy ? PackIconKind.Stop : create ? PackIconKind.PackageDown : PackIconKind.PackageUp;
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _loading) return;
            _settings.Format = Enum.TryParse(TagOf(CmbFormat), out ArchiveFormat f) ? f : ArchiveFormat.SevenZip;
            _settings.Level = Enum.TryParse(TagOf(CmbLevel), out ArchiveLevel l) ? l : ArchiveLevel.Normal;
            _settings.Split = ChkSplit.IsChecked == true;
            if (SevenZip.TryParseSize(TxtSplit.Text, out _)) _settings.SplitSize = TxtSplit.Text.Trim();
            _settings.HideNames = ChkHideNames.IsChecked == true;
            ArchiveManager.SaveSettings(_settings);
            UpdateOptionState();
        }

        private void Option_PasswordChanged(object sender, RoutedEventArgs e) => UpdateOptionState();

        private void UpdateOptionState()
        {
            if (PanelSplitSize == null) return;
            var format = Enum.TryParse(TagOf(CmbFormat), out ArchiveFormat f) ? f : ArchiveFormat.SevenZip;
            PanelSplitSize.Visibility = ChkSplit.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PwdCreate.IsEnabled = format != ArchiveFormat.Tar;
            ChkHideNames.IsEnabled = format == ArchiveFormat.SevenZip && PwdCreate.Password.Length > 0;
            TxtPasswordNote.Text = format switch
            {
                ArchiveFormat.Tar => Loc.T("Str_Archive_PasswordNoTar", "TAR files cannot be locked with a password. Choose 7z or ZIP."),
                ArchiveFormat.Zip => Loc.T("Str_Archive_PasswordZip", "The ZIP will use AES-256. File names inside a ZIP stay visible; choose 7z to hide them."),
                _ => Loc.T("Str_Archive_PasswordCreateNote", "Leave empty for no password. Lose the password and the files cannot be recovered."),
            };
        }

        private void Split_Chosen(object? sender, string value)
        {
            TxtSplit.Text = value;
            ChkSplit.IsChecked = true;
            Option_Changed(this, new RoutedEventArgs());
        }

        // =================================================================
        //  Open / extract: choosing the archive
        // =================================================================

        private async void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (_busy) return;
            string? file = e.Paths.FirstOrDefault(File.Exists);
            if (file == null)
            {
                ShowStatus(Loc.T("Str_Archive_PickFile", "Choose an archive file (not a folder)."), StatusKind.Warning);
                return;
            }
            InputCard.PopDropArea();
            await SetArchiveAsync(file);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (!_busy) ClearArchive();
        }

        private void BtnRemovePath_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || sender is not FrameworkElement { Tag: FolderListEntry entry }) return;
            if (_openList.Remove(entry)) ClearArchive();
            else _createList.Remove(entry);
            UpdateEmpty();
        }

        private void ClearArchive()
        {
            _archive = "";
            _split = null;
            _listing = null;
            _openList.Clear();
            ShowList(null);
            HideStatus();
            ResetProgress();
            UpdateEmpty();
        }

        private async Task SetArchiveAsync(string path)
        {
            _archive = path;
            _openList.Clear();
            _openList.Add(new FolderListEntry { FullPath = path, IsFolder = false });
            _listing = null;
            ShowList(null);
            await LoadListingAsync();
        }

        private void PwdOpen_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _busy || _archive.Length == 0) return;
            e.Handled = true;
            _ = LoadListingAsync();   // Enter in the password box: try the archive with it
        }

        /// <summary>Reads the archive's list (not its content). Says what is wrong: parts missing, a locked archive, a broken file.</summary>
        private async Task<bool> LoadListingAsync()
        {
            if (_archive.Length == 0 || _busy) return false;
            if (!SevenZip.CanUsePassword(PwdOpen.Password))
            {
                ShowStatus(Loc.T("Str_Archive_PasswordQuote", "7-Zip cannot take a password that contains a double quote (\")."), StatusKind.Warning);
                return false;
            }
            _cts = new CancellationTokenSource();
            SetBusy(true);
            Bar.IsIndeterminate = true;
            TxtProgress.Text = Loc.T("Str_Archive_Reading", "Reading the archive...");
            HideStatus();
            ArchiveListResult result;
            try
            {
                result = await ArchiveManager.ListAsync(_archive, PwdOpen.Password, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
                ResetProgress();
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"Archive Manager: could not read {_archive}", ex);
                SetBusy(false);
                ResetProgress();
                ShowStatus(Loc.F("Str_Archive_ReadFailed", "The archive could not be read: {0}", ex.Message), StatusKind.Error);
                return false;
            }
            SetBusy(false);
            ResetProgress();
            _split = result.Split;

            switch (result.State)
            {
                case ArchiveListState.Ok:
                    _listing = result.Listing;
                    ShowList(result.Listing);
                    ShowSplitInfo(result.Split, result.Listing!);
                    return true;
                case ArchiveListState.MissingParts:
                    _listing = null;
                    ShowList(null);
                    ShowStatus(Loc.F("Str_Archive_MissingParts", "This archive is split into parts and some are not in the folder: {0}. Put all the parts in the same folder, with their original names, and open it again.", result.Error), StatusKind.Error);
                    return false;
                case ArchiveListState.NeedsPassword:
                    _listing = null;
                    ShowList(null);
                    ShowStatus(PwdOpen.Password.Length > 0
                        ? Loc.T("Str_Archive_WrongPassword", "That password does not open this archive.")
                        : Loc.T("Str_Archive_Locked", "This archive is locked with a password. Type it in Password and press Enter."), StatusKind.Warning);
                    PwdOpen.Focus();
                    return false;
                case ArchiveListState.NoEngine:
                    ShowStatus(Loc.T("Str_Archive_NoEngine", "The 7-Zip program that Archive Manager uses is missing from this copy of Smart Tools (Engines\\7ZIP)."), StatusKind.Error);
                    return false;
                default:
                    _listing = null;
                    ShowList(null);
                    ShowStatus(Loc.F("Str_Archive_NotArchive", "This file could not be opened as an archive: {0}", result.Error), StatusKind.Error);
                    return false;
            }
        }

        private void ShowSplitInfo(SplitSet split, ArchiveListing listing)
        {
            if (split.IsSplit)
                ShowStatus(Loc.F("Str_Archive_SplitOk", "A split archive: {0} parts found, all there. Extract it from any one of them; keep all the parts together in one folder.", split.Parts.Count), StatusKind.Info);
            else if (listing.Info.IsSplit)
                ShowStatus(Loc.F("Str_Archive_SplitOk", "A split archive: {0} parts found, all there. Extract it from any one of them; keep all the parts together in one folder.", listing.Info.Volumes), StatusKind.Info);
        }

        // =================================================================
        //  The list
        // =================================================================

        private void ShowList(ArchiveListing? listing)
        {
            _rows.Clear();
            if (listing != null)
                foreach (var entry in listing.Entries) _rows.Add(new ArchiveRow(entry));

            bool has = listing != null;
            SummaryPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (has)
            {
                TxtSumFiles.Text = FilesText.Files(listing!.Files);
                TxtSumFolders.Text = FilesText.Folders(listing.Folders);
                PillFolders.Visibility = listing.Folders > 0 ? Visibility.Visible : Visibility.Collapsed;
                TxtSumSize.Text = OutputFolderManager.FormatBytes(listing.TotalSize);
                int parts = _split is { IsSplit: true } ? _split.Parts.Count : listing.Info.IsSplit ? listing.Info.Volumes : 0;
                PillParts.Visibility = parts > 1 ? Visibility.Visible : Visibility.Collapsed;
                TxtSumParts.Text = Loc.F("Str_Archive_SumParts", "{0} parts", parts);
                int locked = listing.Entries.Count(x => x.Encrypted && !x.IsFolder);
                PillLocked.Visibility = locked > 0 ? Visibility.Visible : Visibility.Collapsed;
                TxtSumLocked.Text = Loc.F("Str_Archive_SumLocked", "{0} locked", locked);
            }
            BtnTest.IsEnabled = has;
            BtnExtractSelected.IsEnabled = has && GridEntries.SelectedItems.Count > 0 && !Creating;
            UpdateEmpty();
        }

        private void GridEntries_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            BtnExtractSelected.IsEnabled = _listing != null && GridEntries.SelectedItems.Count > 0 && !_busy && !Creating;

        private void UpdateEmpty()
        {
            if (EmptyState == null) return;
            EmptyState.Visibility = _rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            bool create = Creating;
            TxtEmptyTitle.Text = create ? Loc.T("Str_Archive_EmptyCreateTitle", "Nothing packed yet") : Loc.T("Str_Archive_EmptyTitle", "Open an archive");
            TxtEmptyHint.Text = create
                ? Loc.T("Str_Archive_EmptyCreateHint", "Add files and folders on the left, choose the format, and press Create archive. The new archive's contents will show here.")
                : Loc.T("Str_Archive_EmptyHint", "Drop a .zip, .7z, .rar, .tar or a split archive (.001, .z01, .part1.rar) here, or add it on the left. You see what is inside before anything is extracted.");
            if (!_busy) TxtProgress.Text = create
                ? (_createList.Count == 0 ? Loc.T("Str_Archive_ChooseFiles", "Add the files to pack.") : Loc.T("Str_Archive_ReadyCreate", "Ready - press Create archive."))
                : (_archive.Length == 0 ? Loc.T("Str_Archive_ChooseArchive", "Choose an archive to start.") : _listing != null ? Loc.T("Str_Archive_ReadyExtract", "Ready - press Extract all, or pick files first.") : "");
        }

        // =================================================================
        //  Create: choosing what to pack
        // =================================================================

        private void CreateInput_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (_busy) return;
            foreach (string path in e.Paths)
            {
                bool folder = Directory.Exists(path);
                if (!folder && !File.Exists(path)) continue;
                if (_createList.Any(x => x.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                _createList.Add(new FolderListEntry { FullPath = path, IsFolder = folder });
            }
            if (_createList.Count > 0 && TxtName.Text.Trim().Length == 0)
                TxtName.Text = _createList.Count == 1 ? Path.GetFileNameWithoutExtension(_createList[0].Name.TrimEnd('\\')) : Loc.T("Str_Archive_DefaultName", "Archive");
            CreateInput.PopDropArea();
            UpdateEmpty();
        }

        private void CreateInput_ClearRequested(object? sender, EventArgs e)
        {
            if (_busy) return;
            _createList.Clear();
            UpdateEmpty();
        }

        // =================================================================
        //  Run
        // =================================================================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();   // the same button stops what runs
                return;
            }
            if (!ArchiveManager.IsAvailable)
            {
                ShowStatus(Loc.T("Str_Archive_NoEngine", "The 7-Zip program that Archive Manager uses is missing from this copy of Smart Tools (Engines\\7ZIP)."), StatusKind.Error);
                return;
            }
            if (Creating) await CreateAsync();
            else await ExtractAsync(null);
        }

        private async void BtnExtractSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _listing == null) return;
            var picked = GridEntries.SelectedItems.OfType<ArchiveRow>().Select(r => r.Path).ToList();
            if (picked.Count == 0) return;
            await ExtractAsync(picked);
        }

        private async Task ExtractAsync(IReadOnlyList<string>? only)
        {
            if (_archive.Length == 0)
            {
                ShowStatus(Loc.T("Str_Archive_ChooseArchive", "Choose an archive to start."), StatusKind.Warning);
                InputCard.PopDropArea();
                return;
            }
            if (_listing == null && !await LoadListingAsync()) return;   // the reasons are already on screen
            if (OutputCard.Status.IsError)
            {
                ShowStatus(Loc.F("Str_Archive_CantSaveThere", "Can't save there: {0} Choose another folder.", OutputCard.Status.Text), StatusKind.Error);
                return;
            }

            string start = _split?.Start ?? _archive;
            string destination = OutputCard.EffectiveFolder ?? Path.GetDirectoryName(_archive) ?? "";
            long needed = only == null ? _listing!.TotalSize : _rows.Where(r => only.Contains(r.Path)).Sum(r => r.Size);
            if (OutputFolderManager.FreeSpace(Directory.Exists(destination) ? destination : Path.GetPathRoot(destination) ?? destination) is { } free && free < needed)
            {
                ShowStatus(Loc.F("Str_Archive_NoSpace", "Not enough free space there: {0} needed, {1} free.", OutputFolderManager.FormatBytes(needed), OutputFolderManager.FormatBytes(free)), StatusKind.Error);
                return;
            }

            _cts = new CancellationTokenSource();
            HideStatus();
            SetBusy(true);
            TxtProgress.Text = Loc.T("Str_Archive_Extracting", "Extracting...");
            var progress = new Progress<int>(ShowPercent);
            try
            {
                var (result, folder) = await ArchiveManager.ExtractAsync(start, destination, PwdOpen.Password, only, progress, _cts.Token);
                SetBusy(false);
                if (result.Cancelled)
                {
                    ResetProgress();
                    TxtProgress.Text = Loc.T("Str_Archive_Stopped", "Stopped. Nothing was left behind.");
                    return;
                }
                if (!result.Ok)
                {
                    ResetProgress();
                    ShowStatus(result.NeedsPassword
                        ? Loc.T("Str_Archive_WrongPassword", "That password does not open this archive.")
                        : Loc.F("Str_Archive_ExtractFailed", "Extracting failed: {0}", result.Error), StatusKind.Error);
                    return;
                }
                ShowPercent(100);
                _resultPath = folder;
                BtnShowResult.IsEnabled = true;
                int count = only == null ? _listing!.Files : _rows.Count(r => only.Contains(r.Path) && !r.IsFolder);
                ShowStatus(Loc.F("Str_Archive_Extracted", "Extracted to {0}. Files that were already there were not replaced.", folder)
                    + (result.Warning.Length > 0 ? " " + Loc.F("Str_Archive_Warning", "Note: {0}", result.Warning) : ""), result.Warning.Length > 0 ? StatusKind.Warning : StatusKind.Success);
                TxtProgress.Text = Loc.N(count, "Str_Archive_DoneOne", "{0:N0} file extracted.", "Str_Archive_DoneMany", "{0:N0} files extracted.", count);
                if (_settings.OpenFolderWhenDone) ShellExplorer.Show(folder);
            }
            catch (Exception ex)
            {
                Log.Warn($"Archive Manager: could not extract {_archive}", ex);
                SetBusy(false);
                ResetProgress();
                ShowStatus(Loc.F("Str_Archive_ExtractFailed", "Extracting failed: {0}", ex.Message), StatusKind.Error);
            }
        }

        private async Task CreateAsync()
        {
            if (_createList.Count == 0)
            {
                ShowStatus(Loc.T("Str_Archive_ChooseFiles", "Add the files to pack."), StatusKind.Warning);
                CreateInput.PopDropArea();
                return;
            }
            if (OutputCard.Status.IsError)
            {
                ShowStatus(Loc.F("Str_Archive_CantSaveThere", "Can't save there: {0} Choose another folder.", OutputCard.Status.Text), StatusKind.Error);
                return;
            }
            Option_Changed(this, new RoutedEventArgs());
            if (_settings.Split && !SevenZip.TryParseSize(_settings.SplitSize, out _))
            {
                ShowStatus(Loc.T("Str_Archive_BadSplit", "The part size is not a size. Try 700 MB or 4 GB."), StatusKind.Warning);
                TxtSplit.Focus();
                return;
            }

            var inputs = _createList.Select(x => x.FullPath).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (inputs.Count == 0)
            {
                ShowStatus(Loc.T("Str_Archive_InputsGone", "The files you added are no longer there."), StatusKind.Error);
                return;
            }
            string folder = OutputCard.EffectiveFolder ?? Path.GetDirectoryName(inputs[0]) ?? "";
            string name = TxtName.Text.Trim().Length > 0 ? TxtName.Text.Trim() : Loc.T("Str_Archive_DefaultName", "Archive");
            string? password = PwdCreate.IsEnabled && PwdCreate.Password.Length > 0 ? PwdCreate.Password : null;
            if (!SevenZip.CanUsePassword(password))
            {
                ShowStatus(Loc.T("Str_Archive_PasswordQuote", "7-Zip cannot take a password that contains a double quote (\")."), StatusKind.Warning);
                PwdCreate.Focus();
                return;
            }

            _cts = new CancellationTokenSource();
            HideStatus();
            SetBusy(true);
            Bar.IsIndeterminate = true;
            TxtProgress.Text = Loc.T("Str_Archive_Measuring", "Measuring what is to be packed...");
            try
            {
                long total = await Task.Run(() => InputSize(inputs, _cts.Token), _cts.Token);
                if (OutputFolderManager.FreeSpace(Directory.Exists(folder) ? folder : Path.GetPathRoot(folder) ?? folder) is { } free && free < total)
                {
                    SetBusy(false);
                    ResetProgress();
                    ShowStatus(Loc.F("Str_Archive_NoSpace", "Not enough free space there: {0} needed, {1} free.", OutputFolderManager.FormatBytes(total), OutputFolderManager.FormatBytes(free)), StatusKind.Error);
                    return;
                }

                Bar.IsIndeterminate = false;
                TxtProgress.Text = Loc.T("Str_Archive_Packing", "Packing...");
                var result = await ArchiveManager.CreateAsync(inputs, folder, name, _settings, password, new Progress<int>(ShowPercent), _cts.Token);
                SetBusy(false);
                if (result.Cancelled)
                {
                    ResetProgress();
                    TxtProgress.Text = Loc.T("Str_Archive_Stopped", "Stopped. Nothing was left behind.");
                    return;
                }
                if (!result.Ok)
                {
                    ResetProgress();
                    ShowStatus(Loc.F("Str_Archive_CreateFailed", "The archive could not be made: {0}", result.Error), StatusKind.Error);
                    return;
                }

                ShowPercent(100);
                long made = result.Files.Sum(f => SafeIo.TryGetLength(f, out long len) ? len : 0);
                _resultPath = result.Files[0];
                BtnShowResult.IsEnabled = true;
                string saved = result.Files.Count > 1
                    ? Loc.F("Str_Archive_CreatedParts", "Made {0} in {1} parts ({2}). Keep all the parts together; open any one to extract.", Path.GetFileName(result.Files[0])[..^4], result.Files.Count, OutputFolderManager.FormatBytes(made))
                    : Loc.F("Str_Archive_Created", "Made {0} ({1}).", Path.GetFileName(result.Files[0]), OutputFolderManager.FormatBytes(made));
                ShowStatus(saved + (result.Error.Length > 0 ? " " + Loc.F("Str_Archive_Warning", "Note: {0}", result.Error) : ""), result.Error.Length > 0 ? StatusKind.Warning : StatusKind.Success);
                if (_settings.OpenFolderWhenDone) ShellExplorer.Show(result.Files[0]);

                // show what is inside the new archive, like opening it
                _archive = result.Files[0];
                _split = null;
                var listed = await ArchiveManager.ListAsync(_archive, password, CancellationToken.None);
                if (listed.State == ArchiveListState.Ok)
                {
                    _listing = listed.Listing;
                    _split = listed.Split;
                    ShowList(listed.Listing);
                }
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
                ResetProgress();
                TxtProgress.Text = Loc.T("Str_Archive_Stopped", "Stopped. Nothing was left behind.");
            }
            catch (Exception ex)
            {
                Log.Warn("Archive Manager: could not create the archive", ex);
                SetBusy(false);
                ResetProgress();
                ShowStatus(Loc.F("Str_Archive_CreateFailed", "The archive could not be made: {0}", ex.Message), StatusKind.Error);
            }
        }

        private static long InputSize(IReadOnlyList<string> inputs, CancellationToken token)
        {
            long total = 0;
            foreach (string input in inputs)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(input))
                {
                    if (SafeIo.TryGetLength(input, out long len)) total += len;
                    continue;
                }
                foreach (string file in SafeIo.EnumerateFiles(input, true))
                {
                    token.ThrowIfCancellationRequested();
                    if (SafeIo.TryGetLength(file, out long len)) total += len;
                }
            }
            return total;
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _archive.Length == 0 || _listing == null) return;
            _cts = new CancellationTokenSource();
            HideStatus();
            SetBusy(true);
            TxtProgress.Text = Loc.T("Str_Archive_Testing", "Checking every file in the archive...");
            try
            {
                var result = await ArchiveManager.TestAsync(_split?.Start ?? _archive, PwdOpen.Password, new Progress<int>(ShowPercent), _cts.Token);
                SetBusy(false);
                if (result.Cancelled)
                {
                    ResetProgress();
                    return;
                }
                if (result.Ok)
                {
                    ShowPercent(100);
                    ShowStatus(Loc.T("Str_Archive_TestOk", "The archive is fine: every file read back correctly."), StatusKind.Success);
                }
                else
                {
                    ResetProgress();
                    ShowStatus(Loc.F("Str_Archive_TestBad", "The archive has a problem: {0}", result.Error), StatusKind.Error);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Archive Manager: could not test {_archive}", ex);
                SetBusy(false);
                ResetProgress();
                ShowStatus(Loc.F("Str_Archive_TestBad", "The archive has a problem: {0}", ex.Message), StatusKind.Error);
            }
        }

        private void BtnShowResult_Click(object sender, RoutedEventArgs e)
        {
            if (_resultPath.Length > 0) ShellExplorer.Show(_resultPath);
        }

        // =================================================================
        //  State and messages
        // =================================================================

        private void ShowPercent(int percent)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = percent;
            TxtPercent.Text = percent + "%";
        }

        private void ResetProgress()
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 0;
            TxtPercent.Text = "";
            UpdateEmpty();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SidebarStack.IsEnabled = !busy;
            BtnTest.IsEnabled = !busy && _listing != null;
            BtnExtractSelected.IsEnabled = !busy && _listing != null && GridEntries.SelectedItems.Count > 0 && !Creating;
            UpdateRunButton();
            if (!busy) Bar.IsIndeterminate = false;
        }

        private enum StatusKind { Info, Success, Warning, Error }

        private void ShowStatus(string text, StatusKind kind)
        {
            (string bg, string fg, PackIconKind icon) = kind switch
            {
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                StatusKind.Warning => ("StatusWarningBg", "StatusWarningFg", PackIconKind.AlertOutline),
                StatusKind.Success => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.CheckCircleOutline),
                _ => ("StatusInfoBg", "StatusInfoFg", PackIconKind.InformationOutline),
            };
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            StatusIcon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, fg);
            StatusIcon.Kind = icon;
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        /// <summary>Called when the window closes: stops 7-Zip. A run that is stopped removes what it had started.</summary>
        public void CancelActiveRun() => _cts?.Cancel();
    }
}
