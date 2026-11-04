using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
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

        public FolderTreeView()
        {
            InitializeComponent();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            UpdateState();
        }

        // =================================================================
        //  Options
        // =================================================================

        private FolderTreeOptions ReadOptions() => new()
        {
            Folder = InputCard.FolderPath,
            Format = RadioMarkdown.IsChecked == true ? FolderTreeFormat.Markdown
                : RadioJson.IsChecked == true ? FolderTreeFormat.Json
                : RadioCsv.IsChecked == true ? FolderTreeFormat.Csv
                : FolderTreeFormat.Text,
            MaxDepth = Math.Max(0, CmbDepth.SelectedIndex),
            IncludeFiles = ChkFiles.IsChecked == true,
            ShowSize = ChkSize.IsChecked == true,
            ShowDate = ChkDate.IsChecked == true,
            IncludeHidden = ChkHidden.IsChecked == true,
            SkipNoise = ChkNoise.IsChecked == true,
        };

        private void InputCard_FolderChanged(object? sender, EventArgs e)
        {
            if (_busy) return;
            ClearResult();
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
                ShowStatus("Choose the folder to export.", StatusKind.Warning);
                InputCard.PopDropArea();
                return;
            }

            _cts = new CancellationTokenSource();
            HideStatus();
            ClearResult();
            SetBusy(true);
            Bar.IsIndeterminate = true;
            TxtProgress.Text = "Reading the folder...";

            FolderTreeResult result;
            try
            {
                var progress = new Progress<FolderTreeProgress>(p =>
                    TxtProgress.Text = $"{p.Folders:N0} folders · {p.Files:N0} files · {p.Current}");
                result = await FolderTreeManager.ScanAsync(o, progress, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
                TxtProgress.Text = "Stopped.";
                UpdateState();
                return;
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowStatus($"The folder couldn't be read: {ex.Message}", StatusKind.Error);
                return;
            }

            SetBusy(false);
            _result = result;
            _scanned = o;
            Bar.Value = 100;
            TxtProgressPercent.Text = "100%";
            ShowTree(result, ReadOptions());
            TxtProgress.Text = $"Done in {result.Elapsed.TotalSeconds:0.0} s.";
            if (result.Unreadable > 0)
                ShowStatus($"{result.Unreadable:N0} folder{(result.Unreadable == 1 ? "" : "s")} couldn't be opened and {(result.Unreadable == 1 ? "is" : "are")} marked 'no access'.", StatusKind.Warning);
        }

        private void ShowTree(FolderTreeResult result, FolderTreeOptions o)
        {
            _text = FolderTreeManager.Render(result, o);
            TxtPreview.Text = _text;
            TxtPreview.ScrollToHome();
            TxtSummaryFolders.Text = $"{result.Folders:N0} folder{(result.Folders == 1 ? "" : "s")}";
            TxtSummaryFiles.Text = $"{result.Files:N0} file{(result.Files == 1 ? "" : "s")}";
            TxtSummarySize.Text = CopyQueueManager.FormatSize(result.Bytes);
            TxtSummaryUnreadable.Text = $"{result.Unreadable:N0} no access";
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
                TxtProgress.Text = "Copied to the clipboard.";
            }
            catch (Exception ex)
            {
                ShowStatus($"The clipboard is busy: {ex.Message}", StatusKind.Error);
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
                FileName = $"{SafeName(_result.Root.Name)} tree{ext}",
                Filter = $"{o.Format} (*{ext})|*{ext}",
                DefaultExt = ext,
                OverwritePrompt = false,   // never replaced - a taken name gets ' (2)'
            };
            if (dialog.ShowDialog() != true) return;

            BtnSave.IsEnabled = false;
            try
            {
                string written = await FolderTreeManager.SaveAsync(_text, dialog.FileName);
                TxtProgress.Text = $"Saved {Path.GetFileName(written)}";
                ShellExplorer.Show(written);
            }
            catch (Exception ex)
            {
                ShowStatus($"The file couldn't be saved: {ex.Message}", StatusKind.Error);
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
            TxtStart.Text = busy ? "Stop" : "Build tree";
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
                if (!_busy) TxtProgress.Text = string.IsNullOrWhiteSpace(InputCard.FolderPath) ? "Choose a folder to start." : "Ready - press Build tree.";
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
