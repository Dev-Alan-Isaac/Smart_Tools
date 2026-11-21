using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Copy Queue - UI only. Planning, the verified copy/move, pause, undo and the saved job are in
    /// <see cref="CopyQueueManager"/>; the safety rules are in <see cref="SafeFileOps"/>.
    /// </summary>
    public partial class CopyQueueView : UserControl
    {
        private static string DialogTitle => Loc.T("Str_Tool_copy-queue_Name", "Copy Queue");

        private readonly CopyQueueManager _manager = new();
        private readonly ObservableCollection<CopyQueueSource> _sources = new();

        private CancellationTokenSource? _cts;
        private CopyQueueJournal? _journal;
        private bool _busy;
        private bool _loadedOnce;
        private bool _restoring;
        private bool _hasResult;
        private IReadOnlyList<CopyQueueItem> _items = Array.Empty<CopyQueueItem>();

        public CopyQueueView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _sources;
            _sources.CollectionChanged += (_, _) => InputCard.SetEmpty(_sources.Count == 0);
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _journal = CopyQueueManager.LoadJournal();
            BtnUndo.Visibility = _journal != null ? Visibility.Visible : Visibility.Collapsed;
            RestoreSavedJob();
            UpdateState();
        }

        /// <summary>A run that was cut short (the app closed, Stop) comes back so Start carries on; finished files are recognised and skipped.</summary>
        private void RestoreSavedJob()
        {
            var job = CopyQueueManager.LoadJob();
            if (job == null) return;
            var alive = job.Sources.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (alive.Count == 0)
            {
                CopyQueueManager.DeleteJob();
                return;
            }

            _restoring = true;
            foreach (string p in alive) _sources.Add(new CopyQueueSource(p));
            (job.Mode == CopyQueueMode.Move ? RadioMove : RadioCopy).IsChecked = true;
            (job.Conflict == CopyQueueConflict.Skip ? RadioSkip : RadioKeepBoth).IsChecked = true;
            ChkVerify.IsChecked = job.Verify;
            ApplyModeCards(animate: false);
            if (Directory.Exists(job.DestinationFolder)) ActiveDestination.FolderPath = job.DestinationFolder;
            _restoring = false;
            ShowStatus(Loc.T("Str_CopyQueue_MsgInterrupted", "Your last queue was interrupted. Press Start to continue - files that were already done are recognised and skipped."), StatusKind.Warning);
        }

        // =================================================================
        //  Sources, destination, options
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (_busy) return;
            foreach (string path in e.Paths)
            {
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                if (_sources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new CopyQueueSource(path));
            }
            ResetResults();
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_busy) return;
            _sources.Clear();
            ResetResults();
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || (sender as FrameworkElement)?.Tag is not CopyQueueSource source) return;
            _sources.Remove(source);
            ResetResults();
        }

        private void DestinationCard_FolderChanged(object? sender, EventArgs e)
        {
            if (!_busy && !_restoring) ResetResults();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _busy || _restoring) return;
            ApplyModeCards(animate: true);
            UpdateState();
        }

        /// <summary>The card for the current mode: "Move to" while Move is chosen, otherwise "Copy to". Each keeps its own folder.</summary>
        private InputFolderCard ActiveDestination => RadioMove.IsChecked == true ? MoveDestinationCard : DestinationCard;

        private void ApplyModeCards(bool animate)
        {
            bool move = RadioMove.IsChecked == true;
            var shown = move ? MoveDestinationCard : DestinationCard;
            var hidden = move ? DestinationCard : MoveDestinationCard;
            hidden.Visibility = Visibility.Collapsed;
            bool wasHidden = shown.Visibility != Visibility.Visible;
            shown.Visibility = Visibility.Visible;
            if (animate && wasHidden) UiMotion.Pop(shown, 0.97);
        }

        private void ResetResults()
        {
            if (_busy) return;
            SetItems(Array.Empty<CopyQueueItem>());
            _hasResult = false;
            Bar.Value = 0;
            TxtProgressPercent.Text = "";
            HideStatus();
            UpdateState();
        }

        private void SetItems(IReadOnlyList<CopyQueueItem> items)
        {
            _items = items;
            ItemList.ItemsSource = items.Count == 0 ? null : items;
        }

        private void BtnOpenDestination_Click(object sender, RoutedEventArgs e)
        {
            if (ShellExplorer.Show(ActiveDestination.FolderPath) != ShellExplorer.ShowResult.Shown)
                ThemedMessageBox.Show(Loc.T("Str_MB_ChooseAFolderFirst", "Choose a folder first."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _sources.Clear();
            ResetResults();
        }

        private CopyQueueOptions ReadOptions() => new()
        {
            Mode = RadioMove.IsChecked == true ? CopyQueueMode.Move : CopyQueueMode.Copy,
            Conflict = RadioSkip.IsChecked == true ? CopyQueueConflict.Skip : CopyQueueConflict.KeepBoth,
            Verify = ChkVerify.IsChecked == true,
            DestinationFolder = ActiveDestination.FolderPath,
        };

        // =================================================================
        //  Start / stop / pause
        // =================================================================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                Stop();   // the same button stops a running queue
                return;
            }
            if (_sources.Count == 0)
            {
                ShowStatus(Loc.T("Str_CopyQueue_MsgAddSome", "Add at least one file or folder."), StatusKind.Warning);
                InputCard.PopDropArea();
                return;
            }
            var o = ReadOptions();
            if (string.IsNullOrWhiteSpace(o.DestinationFolder) || !Directory.Exists(o.DestinationFolder))
            {
                ShowStatus(o.Mode == CopyQueueMode.Move ? Loc.T("Str_CopyQueue_MsgChooseMoveTo", "Choose the folder to move to.") : Loc.T("Str_CopyQueue_MsgChooseCopyTo", "Choose the folder to copy to."), StatusKind.Warning);
                ActiveDestination.PopDropArea();
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            HideStatus();
            _hasResult = false;
            SetItems(Array.Empty<CopyQueueItem>());
            SetBusy(true);
            Bar.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.Text = Loc.T("Str_CopyQueue_MsgLooking", "Looking through the files...");
            Bar.IsIndeterminate = true;

            CopyQueuePlan plan;
            try
            {
                var paths = _sources.Select(s => s.Path).ToList();
                plan = await Task.Run(() => CopyQueueManager.BuildPlan(paths, o, token));
            }
            catch (OperationCanceledException)
            {
                FinishUi();
                ShowStatus("Stopped.", StatusKind.Warning);
                return;
            }
            Bar.IsIndeterminate = false;

            if (plan.Items.Count == 0 && plan.Folders.Count == 0)
            {
                FinishUi();
                ShowStatus(plan.Notes.Count > 0 ? string.Join(" ", plan.Notes) : Loc.T("Str_CopyQueue_MsgNoFiles", "There are no files to copy."), StatusKind.Warning);
                return;
            }

            bool isMove = o.Mode == CopyQueueMode.Move;
            var shortfalls = OutputFolderManager.CheckSpace(plan.Items.Select(i =>
                (i.Destination, i.Size, NeedsSpace: !(isMove && SafeFileOps.SameVolume(i.Source, i.Destination)))));
            if (shortfalls.Count > 0)
            {
                FinishUi();
                ShowStatus(OutputFolderManager.DescribeShortfalls(shortfalls) + " Free some space or choose another folder.", StatusKind.Error);
                ActiveDestination.PopDropArea();
                return;
            }

            if (o.Mode == CopyQueueMode.Move)
            {
                var answer = ThemedMessageBox.Show(
                    Loc.N(plan.Items.Count,
                        "Str_MB_CqMoveOne", "Move {0:N0} file ({1}) to\n{2}?\n\nEach original is removed only after its copy has been checked against it. You can undo the move afterwards.",
                        "Str_MB_CqMoveMany", "Move {0:N0} files ({1}) to\n{2}?\n\nEach original is removed only after its copy has been checked against it. You can undo the move afterwards.",
                        plan.Items.Count, CopyQueueManager.FormatSize(plan.Items.Sum(i => i.Size)), o.DestinationFolder),
                    DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    FinishUi();
                    return;
                }
            }

            SetItems(plan.Items);
            UpdateState();
            CopyQueueManager.SaveJob(new CopyQueueSavedJob
            {
                Sources = _sources.Select(s => s.Path).ToList(),
                DestinationFolder = o.DestinationFolder,
                Mode = o.Mode,
                Conflict = o.Conflict,
                Verify = o.Verify,
            });

            var progress = new Progress<CopyQueueProgress>(ShowProgress);
            CopyQueueResult result;
            try
            {
                result = await _manager.RunAsync(plan, o, progress, token);
            }
            catch (Exception ex)
            {
                FinishUi();
                ShowStatus(Loc.F("Str_CopyQueue_MsgQueueStopped", "The queue stopped: {0}", ex.Message), StatusKind.Error);
                return;
            }

            if (result.Cancelled == 0) CopyQueueManager.DeleteJob();   // kept when stopped, so Start can carry on
            if (o.Mode == CopyQueueMode.Move)
            {
                var journal = CopyQueueManager.BuildJournal(plan.Items);
                if (journal.Entries.Count > 0)
                {
                    _journal = journal;
                    try
                    {
                        CopyQueueManager.SaveJournal(journal);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        // Undo still works until the app closes
                    }
                }
            }

            FinishUi();
            ShowResult(result, o.Mode, plan.Notes);
        }

        private void Stop()
        {
            _cts?.Cancel();
            _manager.Resume();   // a paused worker has to wake up to see the stop
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) return;
            if (_manager.IsPaused)
            {
                _manager.Resume();
                HideStatus();
            }
            else
            {
                _manager.Pause();
                ShowStatus("Paused - the file in progress finishes its current block, then waits. Nothing is left half-done on disk.", StatusKind.Warning);
            }
            UpdatePauseButton();
        }

        private void UpdatePauseButton()
        {
            bool paused = _manager.IsPaused;
            BtnPause.ToolTip = paused ? "Resume" : "Pause";
            IconPause.Kind = paused ? PackIconKind.Play : PackIconKind.Pause;
        }

        // =================================================================
        //  Undo
        // =================================================================

        private async void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _journal == null) return;
            var plan = CopyQueueManager.BuildUndoPlan(_journal);
            if (plan.Items.Count == 0)
            {
                CopyQueueManager.DeleteJournal();
                _journal = null;
                BtnUndo.Visibility = Visibility.Collapsed;
                ShowStatus(Loc.T("Str_CopyQueue_MsgNothingToPutBack", "Nothing left to put back - the moved files are gone from where they landed."), StatusKind.Warning);
                return;
            }
            if (ThemedMessageBox.Show(
                    Loc.N(plan.Items.Count,
                        "Str_MB_CqPutBackOne", "Put {0:N0} file back where it came from?\n\nIt is verified on the way, like the original move.",
                        "Str_MB_CqPutBackMany", "Put {0:N0} files back where they came from?\n\nThey are verified on the way, like the original move.",
                        plan.Items.Count),
                    DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            _cts = new CancellationTokenSource();
            HideStatus();
            _hasResult = false;
            Bar.Value = 0;
            TxtProgressPercent.Text = "";
            SetItems(plan.Items);
            SetBusy(true);
            UpdateState();

            var o = new CopyQueueOptions { Mode = CopyQueueMode.Move, Conflict = CopyQueueConflict.KeepBoth, Verify = true };
            CopyQueueResult result;
            try
            {
                result = await _manager.RunAsync(plan, o, new Progress<CopyQueueProgress>(ShowProgress), _cts.Token);
            }
            catch (Exception ex)
            {
                FinishUi();
                ShowStatus(Loc.F("Str_CopyQueue_MsgUndoStopped", "Undo stopped: {0}", ex.Message), StatusKind.Error);
                return;
            }

            // Anything that could not be put back stays in the journal so Undo can be tried again.
            var left = _journal.Entries.Where(en => plan.Items.Any(i => i.Status != CopyQueueStatus.Done && string.Equals(i.Source, en.To, StringComparison.OrdinalIgnoreCase))).ToList();
            if (left.Count == 0)
            {
                CopyQueueManager.DeleteJournal();
                _journal = null;
                BtnUndo.Visibility = Visibility.Collapsed;
            }
            else
            {
                _journal.Entries = left;
                try
                {
                    CopyQueueManager.SaveJournal(_journal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // best effort
                }
            }
            FinishUi();
            ShowResult(result, CopyQueueMode.Move, plan.Notes, undo: true);
        }

        // =================================================================
        //  Progress and state
        // =================================================================

        private void ShowProgress(CopyQueueProgress p)
        {
            double percent = p.BytesTotal > 0
                ? Math.Min(100.0, p.BytesDone * 100.0 / p.BytesTotal)
                : p.FilesTotal > 0 ? p.FilesDone * 100.0 / p.FilesTotal : 0;
            Bar.Value = percent;
            TxtProgressPercent.Text = string.Format(CultureInfo.CurrentCulture, "{0:0}%", percent);
            string speed = p.BytesPerSecond > 0 ? $" · {CopyQueueManager.FormatSize((long)p.BytesPerSecond)}/s" : "";
            TxtProgress.Text = Loc.F("Str_CopyQueue_Progress", "{0:N0} of {1:N0} · {2} of {3}{4} · {5}", p.FilesDone, p.FilesTotal, CopyQueueManager.FormatSize(p.BytesDone), CopyQueueManager.FormatSize(p.BytesTotal), speed, p.CurrentFile);
            UpdateSummary();
        }

        /// <summary>The pills above the list: what is queued and, as the run goes, how each file ended up.</summary>
        private void UpdateSummary()
        {
            int files = _items.Count > 0 ? _items.Count : 0;
            long bytes = _items.Sum(i => i.Size);
            int done = _items.Count(i => i.Status == CopyQueueStatus.Done);
            int already = _items.Count(i => i.Status == CopyQueueStatus.AlreadyThere);
            int skipped = _items.Count(i => i.Status == CopyQueueStatus.Skipped);
            int failed = _items.Count(i => i.Status == CopyQueueStatus.Failed);
            int verified = _items.Count(i => i.Status == CopyQueueStatus.Done && i.Verified);

            TxtSummaryFiles.Text = files > 0
                ? $"{FilesText.Files(files)} · {CopyQueueManager.FormatSize(bytes)}"
                : Loc.N(_sources.Count, "Str_CopyQueue_ItemsOne", "{0:N0} item in the queue", "Str_CopyQueue_ItemsMany", "{0:N0} items in the queue", _sources.Count);
            TxtSummaryDone.Text = (Mode() == CopyQueueMode.Move ? Loc.F("Str_Files_SumMoved", "{0:N0} moved", done) : Loc.F("Str_Files_SumCopied", "{0:N0} copied", done));
            TxtSummaryVerified.Text = Loc.F("Str_CopyQueue_RunVerified", "{0:N0} verified", verified);
            TxtSummaryAlready.Text = Loc.F("Str_CopyQueue_RunAlreadyThere", "{0:N0} already there", already);
            TxtSummarySkipped.Text = Loc.F("Str_CopyQueue_RunSkipped", "{0:N0} skipped", skipped);
            TxtSummaryFailed.Text = Loc.F("Str_CopyQueue_RunFailed", "{0:N0} failed", failed);
            PillDone.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillVerified.Visibility = verified > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillAlready.Visibility = already > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillSkipped.Visibility = skipped > 0 ? Visibility.Visible : Visibility.Collapsed;
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool show = _sources.Count > 0 || files > 0;
            bool firstShow = show && SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);
        }

        private CopyQueueMode Mode() => RadioMove.IsChecked == true ? CopyQueueMode.Move : CopyQueueMode.Copy;

        private void ShowResult(CopyQueueResult r, CopyQueueMode mode, List<string> notes, bool undo = false)
        {
            _hasResult = true;
            Bar.Value = r.Cancelled > 0 ? Bar.Value : 100;
            TxtProgressPercent.Text = r.Cancelled > 0 ? TxtProgressPercent.Text : "100%";
            TxtProgress.Text = CopyQueueManager.DescribeRun(r, undo ? CopyQueueMode.Move : mode, undo);
            UpdateSummary();

            var messages = new List<string>(notes);
            if (r.Failed > 0) messages.Add(Loc.N(r.Failed, "Str_CopyQueue_FailedNoteOne", "{0:N0} file failed - the originals were not touched. Hover a row for details; press Start again to retry.", "Str_CopyQueue_FailedNoteMany", "{0:N0} files failed - the originals were not touched. Hover a row for details; press Start again to retry.", r.Failed));
            if (r.Cancelled > 0) messages.Add("Stopped. Press Start to carry on where it left off.");
            if (messages.Count > 0) ShowStatus(string.Join(" ", messages), r.Failed > 0 ? StatusKind.Error : StatusKind.Warning);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SidebarStack.IsEnabled = !busy;
            BtnPause.IsEnabled = busy;
            BtnUndo.IsEnabled = !busy;
            TxtStart.Text = busy ? "Stop" : "Start";
            IconStart.Kind = busy ? PackIconKind.Stop : PackIconKind.Play;
            UpdatePauseButton();
        }

        private void FinishUi()
        {
            _manager.Resume();
            SetBusy(false);
            Bar.IsIndeterminate = false;
            UpdateState();
        }

        private void UpdateState()
        {
            bool hasItems = _items.Count > 0;
            ListEmpty.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            if (!hasItems)
            {
                TxtEmptyTitle.Text = _sources.Count == 0 ? Loc.T("Str_CopyQueue_EmptyAddTitle", "Add files or folders") : Loc.T("Str_CopyQueue_EmptyReadyTitle", "Ready to start");
                TxtEmptyHint.Text = _sources.Count == 0
                    ? Loc.T("Str_CopyQueue_EmptyAddHint", "Drop them on the left, pick where they go, then press Start.")
                    : Loc.T("Str_CopyQueue_EmptyReadyHint", "Pick the destination if you haven't, then press Start.");
            }

            // The line under the bar says what to do next until a run has something to report.
            if (!_busy && !_hasResult)
                TxtProgress.Text = _sources.Count == 0 ? Loc.T("Str_CopyQueue_MsgAddToStart", "Add files or folders to start.") : Loc.T("Str_CopyQueue_MsgReadyPress", "Ready - press Start.");

            UpdateSummary();
        }

        // =================================================================
        //  Messages
        // =================================================================

        private enum StatusKind { Warning, Error }

        private void ShowStatus(string text, StatusKind kind)
        {
            string bg = kind == StatusKind.Error ? "StatusErrorBg" : "StatusWarningBg";
            string fg = kind == StatusKind.Error ? "StatusErrorFg" : "StatusWarningFg";
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            StatusIcon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, fg);
            StatusIcon.Kind = kind == StatusKind.Error ? PackIconKind.AlertCircleOutline : PackIconKind.AlertOutline;
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        /// <summary>Called when the window closes: stops the queue. Files done stay done, the rest wait in the saved job, no original is touched.</summary>
        public void CancelActiveRun()
        {
            _cts?.Cancel();
            _manager.Resume();
        }
    }
}
