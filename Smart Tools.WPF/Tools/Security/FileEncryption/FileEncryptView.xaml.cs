using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Size = System.Windows.Size;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// File Encryption view - UI only. Which files fit the mode, names, the
    /// password meter and settings are in <see cref="EncryptManager"/>;
    /// the file format in EncryptManager.Crypto.cs; running a batch in
    /// EncryptManager.Run.cs. The password only ever lives in the two
    /// PasswordBoxes and the run's KeyRing - it is never saved.
    /// </summary>
    public partial class FileEncryptView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DialogTitle = "File Encryption";

        private readonly EncryptManager _manager = new();

        /// <summary>What the user added (files and folders) - folders are expanded into <see cref="_queued"/>.</summary>
        private readonly List<EncryptSource> _sources = new();
        private readonly ObservableCollection<EncryptQueueGroup> _queueDisplay = new();
        private List<EncryptManager.QueuedFile> _queued = new();
        private EncryptManager.QueueResult? _lastQueue;
        private EncryptManager.ListResult? _lastList;

        /// <summary>Removed with "Remove selected" - stays removed when the list is rebuilt.</summary>
        private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

        private ObservableCollection<EncryptItem> _rows = new();
        private EncryptRunResult? _lastRun;
        private EncryptMode _lastRunMode;

        private CancellationTokenSource? _runCts;
        private CancellationTokenSource? _expandCts;
        private Task? _expandTask;
        private EncryptSettings _settings = new();

        private bool _isRunning;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;
        private readonly bool _viewConstructed;

        public FileEncryptView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _queueDisplay;
            InputCard.FileFilterProvider = () => Mode == EncryptMode.Encrypt ? "All files (*.*)|*.*" : "Encrypted files (*.enc)|*.enc|All files (*.*)|*.*";
            DataGridFiles.ItemsSource = _rows;
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a run and any folder listing.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _expandCts?.Cancel();
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        private EncryptMode Mode => RadioModeDecrypt.IsChecked == true ? EncryptMode.Decrypt : EncryptMode.Encrypt;

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
            {
                ApplySettings(EncryptManager.LoadSettings());
                _loadedOnce = true;
            }
            ApplyResponsive(new Size(ActualWidth, ActualHeight));
            UpdateQueueHint();
            UpdatePasswordUi();
            UpdateSummary();
            UpdateEmptyState();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsive(e.NewSize);

        /// <summary>Narrow: drop the output column. Short: drop the security note so the list keeps its room.</summary>
        private void ApplyResponsive(Size size)
        {
            ColOutput.Visibility = size.Width < 860 ? Visibility.Collapsed : Visibility.Visible;
            SecurityNote.Visibility = size.Height is > 0 and < 520 ? Visibility.Collapsed : Visibility.Visible;
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

        // =================================================================
        //  Mode
        // =================================================================

        private void RadioMode_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateModeUi(animate: true);
            SaveSettings();
            // Folders are read again: files encrypted a moment ago are now the ones to decrypt.
            _lastRun = null;
            _ = RefreshQueueAsync();
        }

        /// <summary>Labels, hints and which password fields show, for the chosen mode.</summary>
        private void UpdateModeUi(bool animate)
        {
            bool encrypt = Mode == EncryptMode.Encrypt;

            TxtModeHint.Text = encrypt
                ? "Locks files with a password. Each file is saved next to the original as name.enc."
                : "Unlocks files encrypted with this tool (name.enc) using their password.";

            if (animate)
            {
                UiMotion.FadeVisibility(PanelConfirm, encrypt);
                UiMotion.FadeVisibility(PanelStrength, encrypt);
                UiMotion.FadeVisibility(NoRecoveryNote, encrypt);
            }
            else
            {
                PanelConfirm.Visibility = encrypt ? Visibility.Visible : Visibility.Collapsed;
                PanelStrength.Visibility = encrypt ? Visibility.Visible : Visibility.Collapsed;
                NoRecoveryNote.Visibility = encrypt ? Visibility.Visible : Visibility.Collapsed;
            }

            IconSummaryTarget.Kind = encrypt ? PackIconKind.LockOutline : PackIconKind.LockOpenVariantOutline;
            EmptyBadge.Tag = encrypt ? PackIconKind.FileLockOutline : PackIconKind.LockOpenVariantOutline;
            if (!_isRunning) SetRunButton(running: false);
            UpdateDeleteNote();
            UpdatePasswordUi();
        }

        // =================================================================
        //  Files and folders
        // =================================================================

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isRunning && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) InputCard.SetDropHighlight(true);
            e.Handled = true;
        }

        private void Root_DragLeave(object sender, DragEventArgs e) =>
            InputCard.SetDropHighlight(false);

        private void Root_Drop(object sender, DragEventArgs e)
        {
            InputCard.SetDropHighlight(false);
            if (!_isRunning && e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddSources(paths);
            e.Handled = true;
        }

        private void AddSources(IEnumerable<string> paths)
        {
            bool added = false;
            foreach (string path in paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !File.Exists(path)) continue;
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new EncryptSource { FullPath = path, IsFolder = isFolder });
                _removed.Remove(path);
                added = true;
            }
            if (!added) return;
            InputCard.PopDropArea();
            _lastRun = null;
            _ = RefreshQueueAsync();
        }

        private void BtnRemoveQueueGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || sender is not Button { Tag: EncryptQueueGroup group }) return;
            if (group.IsFolder)
                _sources.RemoveAll(s => s.IsFolder && string.Equals(s.FullPath, group.FolderPath, StringComparison.OrdinalIgnoreCase));
            else
                _sources.RemoveAll(s => !s.IsFolder && group.FilePaths.Contains(s.FullPath, StringComparer.OrdinalIgnoreCase));
            _ = RefreshQueueAsync();
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (!_isRunning) AddSources(e.Paths);
        }

        private void BtnClearFiles_Click(object sender, RoutedEventArgs e) => InputCard_ClearRequested(sender, e);

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_isRunning) return;
            _expandCts?.Cancel();
            _sources.Clear();
            _queued = new();
            _lastQueue = null;
            _removed.Clear();
            _lastRun = null;
            RebuildQueueDisplay();
            RebuildList();
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            if (Ignore) return;
            SaveSettings();
            if (_sources.Any(s => s.IsFolder)) _ = RefreshQueueAsync();
            else RebuildQueueDisplay();
        }

        private void RebuildQueueDisplay()
        {
            _queueDisplay.Clear();
            foreach (var group in EncryptManager.BuildQueueGroups(_sources, InputCard.IncludeSubfolders))
                _queueDisplay.Add(group);
            UpdateQueueHint();
        }

        private void UpdateQueueHint() =>
            InputCard.SetEmpty(_queueDisplay.Count == 0);

        /// <summary>Reads the added folders off the UI thread (the old view listed them on it), then rebuilds the grid.</summary>
        private Task RefreshQueueAsync()
        {
            RebuildQueueDisplay();
            _expandCts?.Cancel();
            var cts = _expandCts = new CancellationTokenSource();
            _expandTask = ExpandAsync(cts);
            return _expandTask;
        }

        private async Task ExpandAsync(CancellationTokenSource cts)
        {
            if (_sources.Count == 0)
            {
                _queued = new();
                _lastQueue = null;
                RebuildList();
                return;
            }

            if (_sources.Any(s => s.IsFolder))
            {
                ProgressRun.IsIndeterminate = true;
                TxtProgress.Text = "Reading folders...";
            }
            try
            {
                var result = await EncryptManager.ExpandQueueAsync(_sources.ToList(), InputCard.IncludeSubfolders, cts.Token);
                if (cts.IsCancellationRequested) return;
                _queued = result.Files;
                _lastQueue = result;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Info($"Encryption: listing failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_expandCts, cts)) ProgressRun.IsIndeterminate = false;
            }
            RebuildList();
        }

        // =================================================================
        //  Password
        // =================================================================

        private void PwdPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdatePasswordUi();
        }

        private void PwdConfirmPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdatePasswordUi();
        }

        private void Password_KeyUp(object sender, KeyEventArgs e) => UpdateCapsLock(true);
        private void Password_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateCapsLock(true);
        private void Password_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateCapsLock(PwdPassword.IsKeyboardFocused || PwdConfirmPassword.IsKeyboardFocused);

        private void UpdateCapsLock(bool typing)
        {
            if (!_viewConstructed) return;
            bool show = typing && Keyboard.IsKeyToggled(Key.CapsLock);
            if ((CapsLockWarning.Visibility == Visibility.Visible) != show) UiMotion.FadeVisibility(CapsLockWarning, show);
        }

        /// <summary>Meter (themed segments), hint, and whether the two passwords match.</summary>
        private void UpdatePasswordUi()
        {
            if (!_viewConstructed) return;
            string pw = PwdPassword.Password;
            var rating = EncryptManager.RatePassword(pw);

            var segments = new[] { MeterSeg1, MeterSeg2, MeterSeg3, MeterSeg4 };
            string key = EncryptManager.StrengthBrushKey(rating.Strength);
            for (int i = 0; i < segments.Length; i++)
                segments[i].SetResourceReference(Border.BackgroundProperty, i < (int)rating.Strength ? key : "SidebarInputBorder");

            RunStrengthLabel.Text = rating.Strength == PasswordStrength.Empty ? "" : EncryptManager.StrengthLabel(rating.Strength) + " · ";
            RunStrengthLabel.SetResourceReference(TextElement.ForegroundProperty, rating.Strength == PasswordStrength.Empty ? "SidebarText" : key);
            RunStrengthHint.Text = rating.Hint;

            string confirm = PwdConfirmPassword.Password;
            bool showMatch = Mode == EncryptMode.Encrypt && confirm.Length > 0;
            if (showMatch)
            {
                bool match = confirm == pw;
                IconMatch.Kind = match ? PackIconKind.CheckCircleOutline : PackIconKind.AlertCircleOutline;
                IconMatch.SetResourceReference(ForegroundProperty, match ? "StatusSuccessFg" : "StatusWarningFg");
                TxtMatch.SetResourceReference(TextBlock.ForegroundProperty, match ? "StatusSuccessFg" : "StatusWarningFg");
                TxtMatch.Text = match ? "Passwords match" : pw.StartsWith(confirm, StringComparison.Ordinal) ? "Keep typing..." : "Doesn't match";
            }
            if ((PanelMatch.Visibility == Visibility.Visible) != showMatch) UiMotion.FadeVisibility(PanelMatch, showMatch);
        }

        // =================================================================
        //  Options and settings
        // =================================================================

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateDeleteNote();
            SaveSettings();
        }

        private void UpdateDeleteNote()
        {
            bool on = ChkDeleteOriginal.IsChecked == true;
            TxtDeleteNote.Text = Mode == EncryptMode.Encrypt
                ? "Each original goes to the Recycle Bin once its encrypted copy has been checked. Empty the bin (or use the shredder) so no unencrypted copy is left."
                : "Each .enc file goes to the Recycle Bin once it has been decrypted.";
            if ((TxtDeleteNote.Visibility == Visibility.Visible) != on) UiMotion.FadeVisibility(TxtDeleteNote, on);
        }

        private void ApplySettings(EncryptSettings s)
        {
            _applyingSettings = true;
            try
            {
                _settings = s;
                (s.Mode == EncryptMode.Decrypt ? RadioModeDecrypt : RadioModeEncrypt).IsChecked = true;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                ChkDeleteOriginal.IsChecked = s.DeleteOriginal;
                UpdateModeUi(animate: false);
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_applyingSettings || !_loadedOnce) return;
            _settings.Mode = Mode;
            _settings.IncludeSubfolders = InputCard.IncludeSubfolders;
            _settings.DeleteOriginal = ChkDeleteOriginal.IsChecked == true;
            try
            {
                EncryptManager.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                Log.Info($"Encryption settings not saved: {ex.Message}");
            }
        }

        // =================================================================
        //  The file list
        // =================================================================

        /// <summary>Rebuilds the grid from the listed files (no disk access). A new collection is swapped in.</summary>
        private void RebuildList()
        {
            if (_isRunning || !_viewConstructed) return;

            _lastList = EncryptManager.BuildList(_queued, Mode, _removed);
            _rows = new ObservableCollection<EncryptItem>(_lastList.Items);
            DataGridFiles.ItemsSource = _rows;

            UpdateSummary();
            UpdateEmptyState();
            UpdateReadyText();
        }

        private void UpdateReadyText()
        {
            if (_isRunning) return;
            ProgressRun.IsIndeterminate = false;
            ProgressRun.Value = 0;
            TxtProgressPercent.Text = "";
            if (_rows.Count == 0 && _sources.Count == 0)
            {
                TxtProgress.Text = Mode == EncryptMode.Encrypt
                    ? "Add files, type a password, then press Encrypt."
                    : "Add encrypted files, type their password, then press Decrypt.";
                return;
            }

            int pending = _rows.Count(EncryptManager.IsPending);
            var notes = new List<string> { $"{pending:N0} file{(pending == 1 ? "" : "s")} ready to {(Mode == EncryptMode.Encrypt ? "encrypt" : "decrypt")}" };
            if (_lastList?.LeftOutEncrypted > 0) notes.Add($"{_lastList.LeftOutEncrypted:N0} already encrypted left out");
            if (_lastList?.LeftOutPlain > 0) notes.Add($"{_lastList.LeftOutPlain:N0} not encrypted left out");
            if (_lastQueue?.Inaccessible > 0) notes.Add($"{_lastQueue.Inaccessible:N0} folders couldn't be read");
            if (_lastQueue?.Links > 0) notes.Add($"{_lastQueue.Links:N0} linked folders not followed");
            if (_lastQueue?.Missing > 0) notes.Add($"{_lastQueue.Missing:N0} no longer exist");
            TxtProgress.Text = string.Join(" · ", notes);
        }

        private void UpdateSummary()
        {
            var pending = _rows.Where(EncryptManager.IsPending).ToList();
            TxtSummaryFiles.Text = $"{pending.Count:N0} file{(pending.Count == 1 ? "" : "s")} · {EncryptText.FormatBytes(pending.Sum(i => i.SizeBytes))}";
            TxtSummaryTarget.Text = EncryptManager.DescribeTarget(Mode);

            int skipped = _rows.Count(i => i.Status == EncryptStatus.Skipped);
            TxtSummarySkipped.Text = $"{skipped:N0} skipped";
            PillSkipped.Visibility = skipped > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_lastRun is { } run)
            {
                TxtSummaryDone.Text = $"{run.Done:N0} {(_lastRunMode == EncryptMode.Encrypt ? "encrypted" : "decrypted")}";
                TxtSummaryFailed.Text = $"{run.Failed:N0} failed";
                PillDone.Visibility = run.Done > 0 ? Visibility.Visible : Visibility.Collapsed;
                PillFailed.Visibility = run.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                PillDone.Visibility = Visibility.Collapsed;
                PillFailed.Visibility = Visibility.Collapsed;
            }

            bool show = _rows.Count > 0;
            bool firstShow = show && SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);
        }

        private void UpdateEmptyState()
        {
            bool empty = _rows.Count == 0;
            if (_sources.Count > 0 && empty)
                TxtEmptyHint.Text = Mode == EncryptMode.Encrypt
                    ? "Everything in the added folders is already encrypted. Switch to Decrypt to open those files."
                    : "No encrypted files were found in what you added. Switch to Encrypt to lock them.";
            else
                TxtEmptyHint.SetResourceReference(TextBlock.TextProperty, "Str_Encrypt_DragDropHint");
            UiMotion.FadeVisibility(EmptyStatePanel, empty);
        }

        private void BtnRemoveFiles_Click(object sender, RoutedEventArgs e) => RemoveSelected();

        private void RemoveSelected()
        {
            if (_isRunning) return;
            var selected = DataGridFiles.SelectedItems.Cast<EncryptItem>().ToList();
            if (selected.Count == 0) return;
            foreach (var item in selected)
            {
                _removed.Add(item.FullPath);
                _rows.Remove(item);
            }
            UpdateSummary();
            UpdateEmptyState();
            UpdateReadyText();
        }

        private void DataGridFiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            RemoveSelected();
            e.Handled = true;
        }

        /// <summary>Double-click: the finished file if there is one, else the original, in Explorer.</summary>
        private void DataGridFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataGridFiles.SelectedItem is EncryptItem item) ShowInFolder(item, quiet: false);
        }

        private void BtnShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            var item = DataGridFiles.SelectedItem as EncryptItem
                ?? _rows.FirstOrDefault(i => i.Status == EncryptStatus.Done)
                ?? _rows.FirstOrDefault();
            if (item == null)
            {
                InputCard.PopDropArea();
                return;
            }
            ShowInFolder(item, quiet: false);
        }

        private void ShowInFolder(EncryptItem item, bool quiet)
        {
            string path = item.Status == EncryptStatus.Done && File.Exists(item.OutputPath) ? item.OutputPath! : item.FullPath;
            var shown = ShellExplorer.Show(File.Exists(path) ? path : item.Folder);
            if (shown != ShellExplorer.ShowResult.Shown && !quiet)
                MessageBox.Show("That file or folder no longer exists.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        //  Run
        // =================================================================

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                TxtProgress.Text = "Cancelling - finishing the current block...";
                return;
            }

            if (_expandTask is { IsCompleted: false }) await _expandTask;

            var mode = Mode;
            var pending = _rows.Where(EncryptManager.IsPending).ToList();
            if (pending.Count == 0)
            {
                InputCard.PopDropArea();
                MessageBox.Show(_rows.Count == 0
                        ? "Add some files or a folder first (left side, or drop them anywhere here)."
                        : $"Nothing left to {(mode == EncryptMode.Encrypt ? "encrypt" : "decrypt")} - every file is done or doesn't fit this mode.",
                    DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (EncryptManager.PasswordProblem(mode, PwdPassword.Password, PwdConfirmPassword.Password) is string problem)
            {
                MessageBox.Show(problem, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                (string.IsNullOrEmpty(PwdPassword.Password) ? PwdPassword : PwdConfirmPassword).Focus();
                return;
            }

            if (mode == EncryptMode.Encrypt && EncryptManager.RatePassword(PwdPassword.Password).Strength == PasswordStrength.Weak
                && MessageBox.Show("This password is weak and could be guessed.\n\nEncrypt with it anyway?", DialogTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                PwdPassword.Focus();
                return;
            }

            bool deleteOriginal = ChkDeleteOriginal.IsChecked == true;
            if (deleteOriginal && !ConfirmDeleteOriginals(pending, mode)) return;

            var options = new EncryptOptions
            {
                Mode = mode,
                Password = PwdPassword.Password,
                DeleteOriginal = deleteOriginal,
                DeleteToRecycleBin = true,
            };

            SaveSettings();
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            SetRunning(true);

            try
            {
                var progress = new Progress<EncryptProgress>(ReportProgress);
                var result = await _manager.RunAsync(_rows.ToList(), options, progress, _runCts.Token);
                _lastRun = result;
                _lastRunMode = mode;

                SetRunning(false);
                ProgressRun.Value = result.Cancelled > 0 ? ProgressRun.Value : 100;
                TxtProgressPercent.Text = result.Cancelled > 0 ? "" : "100%";
                TxtProgress.Text = EncryptManager.DescribeRun(result, mode);

                try
                {
                    string verb = mode == EncryptMode.Encrypt ? "Encrypted" : "Decrypted";
                    ToastNotificationManager.ShowToast($"{verb} - Complete",
                        result.Failed > 0
                            ? $"{verb} {result.Done} of {result.Done + result.Failed + result.Cancelled} files - {result.Failed} failed."
                            : $"{verb} {result.Done} file{(result.Done == 1 ? "" : "s")}.");
                }
                catch (Exception ex)
                {
                    Log.Info($"Toast failed: {ex.Message}");
                }

                // One clear message when every file was refused for the password, instead of N identical rows.
                if (mode == EncryptMode.Decrypt && result.Done == 0 && result.Failed > 0
                    && _rows.Where(i => i.Status == EncryptStatus.Failed).All(i => i.Message.StartsWith("Wrong password", StringComparison.Ordinal)))
                {
                    MessageBox.Show("The password didn't open any of the files. Check it (and Caps Lock) and try again.",
                        DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    PwdPassword.Focus();
                    PwdPassword.SelectAll();
                }
            }
            catch (Exception ex)
            {
                SetRunning(false);
                TxtProgress.Text = "Stopped - something went wrong.";
                MessageBox.Show($"Couldn't finish: {ex.Message}", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_isRunning) SetRunning(false);
                UpdateSummary();
            }
        }

        private bool ConfirmDeleteOriginals(IReadOnlyList<EncryptItem> pending, EncryptMode mode)
        {
            int noBin = pending.Count(i => !EncryptManager.HasRecycleBin(i.FullPath));
            string what = mode == EncryptMode.Encrypt ? "is encrypted and checked" : "is decrypted";
            string text = noBin == 0
                ? $"After each file {what}, its original will be moved to the Recycle Bin ({pending.Count:N0} files)."
                  + (mode == EncryptMode.Encrypt ? "\n\nThe unencrypted originals stay readable in the Recycle Bin until you empty it." : "")
                  + "\n\nContinue?"
                : $"After each file {what}, its original will be deleted.\n\n{noBin:N0} of them are on a drive without a Recycle Bin (network or removable) and will be deleted PERMANENTLY.\n\nContinue?";
            return MessageBox.Show(text, DialogTitle, MessageBoxButton.YesNo, noBin == 0 ? MessageBoxImage.Question : MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void ReportProgress(EncryptProgress p)
        {
            if (!_isRunning) return;
            ProgressRun.IsIndeterminate = false;
            ProgressRun.Value = p.OverallPercent;
            TxtProgressPercent.Text = $"{p.OverallPercent:0}%";
            string current = string.IsNullOrEmpty(p.CurrentFile) ? "" : $" · {p.CurrentFile}";
            TxtProgress.Text = $"{p.Completed:N0} of {p.Total:N0} done{current}";
        }

        private void SetRunButton(bool running)
        {
            bool encrypt = Mode == EncryptMode.Encrypt;
            IconRun.Kind = running ? PackIconKind.StopCircleOutline : encrypt ? PackIconKind.LockOutline : PackIconKind.LockOpenVariantOutline;
            if (running) TxtRunLabel.Text = TryFindResource("Str_Common_Cancel") as string ?? "Cancel";
            else TxtRunLabel.Text = encrypt ? "Encrypt" : "Decrypt";
        }

        private Log.ActivityScope? _activity;

        private void SetRunning(bool running)
        {
            _isRunning = running;
            if (running) _activity ??= Log.Activity("file-encrypt");
            else { _activity?.Dispose(); _activity = null; }
            SetRunButton(running);
            UiMotion.Pop(BtnRun, 0.96);

            // Changing mode, files or the password mid-run used to be possible.
            OptionsScrollViewer.IsEnabled = !running;
            BtnRemoveFiles.IsEnabled = !running;
            BtnClearFiles.IsEnabled = !running;

            if (running)
            {
                ProgressRun.IsIndeterminate = true;   // deriving the key takes a moment
                ProgressRun.Value = 0;
                TxtProgressPercent.Text = "0%";
                TxtProgress.Text = Mode == EncryptMode.Encrypt ? "Preparing the key..." : "Starting...";
            }
        }
    }
}
