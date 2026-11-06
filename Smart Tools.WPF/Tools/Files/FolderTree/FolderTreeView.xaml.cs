using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Folder Tree Exporter - UI only. Scanning, rendering and the never-overwrite save are in
    /// <see cref="FolderTreeManager"/>.
    /// </summary>
    public partial class FolderTreeView : UserControl
    {
        private static string DialogTitle => Loc.T("Str_Tool_folder-tree_Name", "Folder Tree Exporter");

        private CancellationTokenSource? _cts;
        private FolderTreeResult? _result;
        private FolderTreeOptions? _scanned;
        private string _text = "";
        private bool _busy;
        private bool _loadedOnce;
        private string _folder = "";
        private readonly ObservableCollection<FolderListEntry> _folderList = new();

        public FolderTreeView()
        {
            InitializeComponent();
            InputCard.ItemsSource = _folderList;
            _folderList.CollectionChanged += (_, _) => InputCard.SetEmpty(_folderList.Count == 0);
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            UpdateFormatHint();
            UpdateState();
        }

        // =================================================================
        //  Options
        // =================================================================

        private FolderTreeOptions ReadOptions() => new()
        {
            Folder = _folder,
            Format = SelectedFormat,
            MaxDepth = Math.Max(0, CmbDepth.SelectedIndex),
            IncludeFiles = ChkFiles.IsChecked == true,
            ShowSize = ChkSize.IsChecked == true,
            ShowDate = ChkDate.IsChecked == true,
            IncludeHidden = ChkHidden.IsChecked == true,
            SkipNoise = ChkNoise.IsChecked == true,
        };

        /// <summary>One folder is exported at a time: a folder added (or dropped) replaces the one shown.</summary>
        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (_busy) return;
            string? folder = e.Paths.FirstOrDefault(Directory.Exists);
            if (folder == null) return;
            InputCard.PopDropArea();
            SetFolder(folder);
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (!_busy && _folder.Length > 0) SetFolder("");
        }

        private void BtnRemoveSingleFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) SetFolder("");
        }

        private void SetFolder(string folder)
        {
            _folder = folder;
            _folderList.Clear();
            if (folder.Length > 0) _folderList.Add(new FolderListEntry { FullPath = folder });
            ClearResult();
        }

        private FolderTreeFormat SelectedFormat =>
            (CmbFormat?.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse(tag, out FolderTreeFormat f) ? f : FolderTreeFormat.Text;

        private void CmbFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFormatHint();
            Option_Changed(sender, e);
        }

        private void UpdateFormatHint()
        {
            if (TxtFormatHint == null) return;
            TxtFormatHint.Text = SelectedFormat switch
            {
                FolderTreeFormat.Markdown => Loc.T("Str_FolderTree_ANestedListFor", "A nested list for READMEs and wikis"),
                FolderTreeFormat.Html => Loc.T("Str_FolderTree_HintHtml", "A web page for any browser: folders fold and unfold."),
                FolderTreeFormat.Json => Loc.T("Str_FolderTree_NestedObjectsForScripts", "Nested objects for scripts"),
                FolderTreeFormat.Xml => Loc.T("Str_FolderTree_HintXml", "Nested folder and file elements, for programs that read XML."),
                FolderTreeFormat.Yaml => Loc.T("Str_FolderTree_HintYaml", "A nested list for config files and scripts."),
                FolderTreeFormat.Csv => Loc.T("Str_FolderTree_OneRowPerItem", "One row per item"),
                FolderTreeFormat.Paths => Loc.T("Str_FolderTree_HintPaths", "One full path per line, for scripts and search tools."),
                FolderTreeFormat.Pdf => Loc.T("Str_FolderTree_HintPdf", "A drawn tree with folder and file icons, split over pages. Any size. The preview on the right is the text version."),
                FolderTreeFormat.Image => Loc.F("Str_FolderTree_HintImage", "A picture of the tree (PNG), up to {0:N0} items; use PDF for bigger trees. The preview on the right is the text version.", FolderTreeVisual.MaxImageRows),
                _ => Loc.T("Str_FolderTree_LikeTheTreeCommand", "Like the tree command"),
            };
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _busy) return;
            var o = ReadOptions();
            // Format, sizes and dates only change how the same scan is written; anything else needs a new scan.
            if (_result != null && _scanned != null
                && o.Folder == _scanned.Folder && o.MaxDepth == _scanned.MaxDepth && o.IncludeFiles == _scanned.IncludeFiles
                && o.IncludeHidden == _scanned.IncludeHidden && o.SkipNoise == _scanned.SkipNoise)
            {
                ShowTree(_result, o);
                return;
            }
            ClearResult();
        }

        private void ClearResult()
        {
            _result = null;
            _scanned = null;
            _text = "";
            TxtPreview.Text = "";
            Bar.Value = 0;
            TxtProgressPercent.Text = "";
            HideStatus();
            UpdateState();
        }

        // =================================================================
        //  Build
        // =================================================================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();   // the same button stops a running scan
                return;
            }
            var o = ReadOptions();
            if (string.IsNullOrWhiteSpace(o.Folder) || !Directory.Exists(o.Folder))
            {
                ShowStatus(Loc.T("Str_FolderTree_MsgChoose", "Choose the folder to export."), StatusKind.Warning);
                InputCard.PopDropArea();
                return;
            }

            _cts = new CancellationTokenSource();
            HideStatus();
            ClearResult();
            SetBusy(true);
            Bar.IsIndeterminate = true;
            TxtProgress.Text = Loc.T("Str_FolderTree_MsgReading", "Reading the folder...");

            FolderTreeResult result;
            try
            {
                var progress = new Progress<FolderTreeProgress>(p =>
                    TxtProgress.Text = Loc.F("Str_FolderTree_Progress", "{0:N0} folders · {1:N0} files · {2}", p.Folders, p.Files, p.Current));
                result = await FolderTreeManager.ScanAsync(o, progress, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
                TxtProgress.Text = Loc.T("Str_FolderTree_MsgStopped", "Stopped.");
                UpdateState();
                return;
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowStatus(Loc.F("Str_FolderTree_MsgReadFailed", "The folder couldn't be read: {0}", ex.Message), StatusKind.Error);
                return;
            }

            SetBusy(false);
            _result = result;
            _scanned = o;
            Bar.Value = 100;
            TxtProgressPercent.Text = "100%";
            ShowTree(result, ReadOptions());
            TxtProgress.Text = Loc.F("Str_FolderTree_MsgDone", "Done in {0:0.0} s.", result.Elapsed.TotalSeconds);
            if (result.Unreadable > 0)
                ShowStatus(Loc.N(result.Unreadable, "Str_FolderTree_UnreadableOne", "{0:N0} folder couldn't be opened and is marked 'no access'.", "Str_FolderTree_UnreadableMany", "{0:N0} folders couldn't be opened and are marked 'no access'.", result.Unreadable), StatusKind.Warning);
        }

        private void ShowTree(FolderTreeResult result, FolderTreeOptions o)
        {
            _text = FolderTreeManager.Render(result, o);
            TxtPreview.Text = _text;
            TxtPreview.ScrollToHome();
            TxtSummaryFolders.Text = FilesText.Folders(result.Folders);
            TxtSummaryFiles.Text = FilesText.Files(result.Files);
            TxtSummarySize.Text = CopyQueueManager.FormatSize(result.Bytes);
            TxtSummaryUnreadable.Text = Loc.F("Str_FolderTree_SumNoAccess", "{0:N0} no access", result.Unreadable);
            PillUnreadable.Visibility = result.Unreadable > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool firstShow = SummaryPanel.Visibility != Visibility.Visible;
            SummaryPanel.Visibility = Visibility.Visible;
            if (firstShow) UiMotion.Pop(SummaryPanel, 0.9);
            UpdateState();
        }

        // =================================================================
        //  Copy and save
        // =================================================================

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_text.Length == 0) return;
            try
            {
                System.Windows.Clipboard.SetText(_text);
                TxtProgress.Text = Loc.T("Str_FolderTree_MsgCopied", "Copied to the clipboard.");
            }
            catch (Exception ex)
            {
                ShowStatus(Loc.F("Str_FolderTree_MsgClipboardBusy", "The clipboard is busy: {0}", ex.Message), StatusKind.Error);
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_text.Length == 0 || _result == null) return;
            var o = ReadOptions();
            string ext = FolderTreeManager.FileExtension(o.Format);
            var dialog = new SaveFileDialog
            {
                Title = DialogTitle,
                FileName = Loc.F("Str_FolderTree_FileName", "{0} tree{1}", SafeName(_result.Root.Name), ext),
                Filter = $"{FolderTreeManager.FormatTitle(o.Format)} (*{ext})|*{ext}",
                DefaultExt = ext,
                OverwritePrompt = false,   // never replaced - a taken name gets ' (2)'
            };
            if (dialog.ShowDialog() != true) return;

            BtnSave.IsEnabled = false;
            try
            {
                string written;
                string saved;
                if (FolderTreeManager.IsVisual(o.Format))
                {
                    // Drawing a big tree takes a moment: off the UI thread, with a message.
                    TxtProgress.Text = Loc.T("Str_FolderTree_MsgDrawing", "Drawing the tree...");
                    var tree = _result!;
                    var (data, drawn, total) = await Task.Run(() =>
                    {
                        if (o.Format == FolderTreeFormat.Pdf)
                        {
                            byte[] pdf = FolderTreeVisual.RenderPdf(tree, o, out int d, out int t);
                            return (pdf, d, t);
                        }
                        byte[] png = FolderTreeVisual.RenderPng(tree, o, CancellationToken.None, out int d2, out int t2);
                        return (png, d2, t2);
                    });
                    written = await FolderTreeManager.SaveBytesAsync(data, dialog.FileName);
                    saved = drawn < total
                        ? Loc.F("Str_FolderTree_MsgSavedPartial", "Saved {0} - the first {1:N0} of {2:N0} items. Use PDF for the whole tree.", Path.GetFileName(written), drawn, total)
                        : Loc.F("Str_FolderTree_MsgSaved", "Saved {0}", Path.GetFileName(written));
                }
                else
                {
                    written = await FolderTreeManager.SaveAsync(_text, dialog.FileName);
                    saved = Loc.F("Str_FolderTree_MsgSaved", "Saved {0}", Path.GetFileName(written));
                }
                TxtProgress.Text = saved;
                ShellExplorer.Show(written);
            }
            catch (Exception ex)
            {
                ShowStatus(Loc.F("Str_FolderTree_MsgSaveFailed", "The file couldn't be saved: {0}", ex.Message), StatusKind.Error);
            }
            finally
            {
                BtnSave.IsEnabled = _text.Length > 0;
            }
        }

        private void TxtPreview_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
            TxtPreview.FontSize = Math.Clamp(TxtPreview.FontSize + (e.Delta > 0 ? 1 : -1), 9, 24);
            e.Handled = true;
        }

        private static string SafeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length == 0 ? "folder" : name;
        }

        // =================================================================
        //  State and messages
        // =================================================================

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SidebarStack.IsEnabled = !busy;
            TxtStart.Text = busy ? Loc.T("Str_FolderTree_Stop", "Stop") : Loc.T("Str_FolderTree_Build", "Build tree");
            IconStart.Kind = busy ? PackIconKind.Stop : PackIconKind.FileTreeOutline;
            if (!busy) Bar.IsIndeterminate = false;
        }

        private void UpdateState()
        {
            bool has = _text.Length > 0;
            PreviewEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            BtnCopy.IsEnabled = has;
            BtnSave.IsEnabled = has;
            if (!has)
            {
                SummaryPanel.Visibility = Visibility.Collapsed;
                if (!_busy) TxtProgress.Text = string.IsNullOrWhiteSpace(_folder) ? Loc.T("Str_FolderTree_MsgChooseStart", "Choose a folder to start.") : Loc.T("Str_FolderTree_MsgReady", "Ready - press Build tree.");
            }
        }

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

        /// <summary>Called when the window closes: stops a scan in progress. The scanned folder is never changed.</summary>
        public void CancelActiveRun() => _cts?.Cancel();
    }
}
