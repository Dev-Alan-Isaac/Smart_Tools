using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Engines;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Image Metadata Cleaner - UI only. Reading and cleaning are in <see cref="MetadataCleanerManager"/>.
    /// Originals are never changed; every result is a new file.
    /// </summary>
    public partial class MetadataCleanerView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_image-metadata-cleaner_Name", "Image Metadata Cleaner");

        private readonly MetadataCleanerManager _cleaner = new();
        private readonly ObservableCollection<MetaItem> _items = new();
        private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _runCts;
        private readonly ThumbnailWindow<MetaItem> _thumbs;
        private MetaCleanSettings _settings = new();
        private bool _viewConstructed, _loadedOnce, _applying, _busy, _optionsCollapsed;

        public MetadataCleanerView()
        {
            InitializeComponent();
            GridItems.ItemsSource = _items;
            _thumbs = new(GridItems, () => _items, i => i.Thumbnail, (i, v) => i.Thumbnail = v,
                async (i, token) => await ThumbnailService.GetThumbnailAsync(i.Path, 80, token));
            InputCard.ItemsSource = _items;
            InputCard.FileFilterProvider = () => T("Str_MetaClean_FilterPictures", "Pictures") + "|*.jpg;*.jpeg;*.jpe;*.png;*.webp;*.tif;*.tiff|" + T("Str_MetaClean_FilterAll", "All files") + "|*.*";
            _items.CollectionChanged += (_, _) => UpdateEmptyAndCount();
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a run (finished pictures stay) and the loaders.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _thumbs.Dispose();
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _settings = MetadataCleanerManager.LoadSettings();
            ApplySettings(_settings);
            UpdateEmptyAndCount();
            ShowIdle();
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? OptionsCollapsedWidth : OptionsExpandedWidth);
        }

        // =================================================================
        //  Settings <-> controls
        // =================================================================

        private void ApplySettings(MetaCleanSettings s)
        {
            _applying = true;
            try
            {
                ModeEverything.IsChecked = s.Mode == CleanMode.Everything;
                ModeLocation.IsChecked = s.Mode == CleanMode.LocationOnly;
                TxtSuffix.Text = s.Suffix;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
            }
            finally
            {
                _applying = false;
            }
            UpdateModeHint();
        }

        private MetaCleanSettings ReadSettings() => MetadataCleanerManager.Sanitize(new MetaCleanSettings
        {
            Mode = ModeLocation.IsChecked == true ? CleanMode.LocationOnly : CleanMode.Everything,
            Suffix = TxtSuffix.Text,
            IncludeSubfolders = InputCard.IncludeSubfolders,
        });

        private void SaveSettings()
        {
            if (!_viewConstructed || _applying) return;
            _settings = ReadSettings();
            _applying = true;
            TxtSuffix.Text = _settings.Suffix;
            _applying = false;
            MetadataCleanerManager.SaveSettings(_settings);
        }

        private void Field_LostFocus(object sender, RoutedEventArgs e) => SaveSettings();

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            UpdateModeHint();
        }

        private void UpdateModeHint() =>
            TxtModeHint.Text = _settings.Mode == CleanMode.LocationOnly
                ? T("Str_MetaClean_HintLocation", "Removes only where the picture was taken. Camera and date stay. The picture is saved again, so JPG files change a tiny bit.")
                : T("Str_MetaClean_HintEverything", "Removes location, camera, date, software and comments. The color profile stays. JPG files are cleaned without any quality loss.");

        // =================================================================
        //  Adding pictures
        // =================================================================

        private async void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (!_busy) await AddPathsAsync(e.Paths);
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e) => SaveSettings();

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_busy) return;
            _thumbs.Reset();
            _items.Clear();
            _known.Clear();
            ShowIdle();
        }

        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            bool include = InputCard.IncludeSubfolders;
            TxtProgress.Text = T("Str_MetaClean_Reading", "Looking inside the pictures…");
            TxtProgressDetail.Text = "";

            var added = await Task.Run(() =>
            {
                var files = BulkImageResizerManager.ScanPictures(paths, include, CancellationToken.None);
                var created = new List<(MetaItem Item, MetaFound? Found)>();
                foreach (string file in files)
                {
                    try
                    {
                        var item = new MetaItem { Path = file, Name = Path.GetFileName(file), OriginalBytes = new FileInfo(file).Length };
                        created.Add((item, MetadataCleanerManager.CanClean(file) ? MetadataCleanerManager.ReadFound(file) : null));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file that vanished or can't be read is simply not added.
                    }
                }
                return created;
            });

            int skippedDuplicates = 0;
            var fresh = new List<MetaItem>();
            foreach (var (item, found) in added)
            {
                if (!_known.Add(item.Path)) { skippedDuplicates++; continue; }
                if (found != null) item.SetFound(found);
                _items.Add(item);
                fresh.Add(item);
            }

            TxtProgress.Text = fresh.Count == 0
                ? T("Str_MetaClean_NoneAdded", "No new pictures were added.")
                : string.Format(T("Str_MetaClean_Added", "Added {0} pictures."), fresh.Count);
            TxtProgressDetail.Text = skippedDuplicates > 0 ? string.Format(T("Str_MetaClean_Duplicates", "{0} were already in the list."), skippedDuplicates) : "";
            UpdateFoundPills();
            _thumbs.Refresh();
        }

        private void UpdateEmptyAndCount()
        {
            PanelEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            InputCard.SetEmpty(_items.Count == 0);
            TxtCount.Text = string.Format(T("Str_MetaClean_Count", "{0} pictures"), _items.Count);
            if (!_busy) BtnRun.IsEnabled = _items.Count > 0;
            UpdateFoundPills();
        }

        /// <summary>"3 with a location" before the run: the thing worth knowing before sharing.</summary>
        private void UpdateFoundPills()
        {
            int withLocation = _items.Count(i => i.HasLocation);
            TxtLocation.Text = string.Format(T("Str_MetaClean_WithLocation", "{0} with location"), withLocation);
            PillLocation.Visibility = withLocation > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowIdle()
        {
            TxtProgress.Text = T("Str_MetaClean_Idle", "Add pictures, choose what to remove, and press Clean.");
            TxtProgressDetail.Text = T("Str_MetaClean_IdleDetail", "Your originals are never changed. Clean copies are saved as new files.");
            ProgressRun.Value = 0;
            PillClean.Visibility = Visibility.Collapsed;
        }

        private void BtnRemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || (sender as FrameworkElement)?.DataContext is not MetaItem item) return;
            _items.Remove(item);
            _known.Remove(item.Path);
        }

        private void BtnShowResult_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not MetaItem { NewPath.Length: > 0 } item || !File.Exists(item.NewPath)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.NewPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Explorer couldn't start; nothing else to do.
            }
        }

        // =================================================================
        //  Run
        // =================================================================

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                _runCts?.Cancel();
                return;
            }
            await RunAsync();
        }

        private async Task RunAsync()
        {
            if (_items.Count == 0) return;
            SaveSettings();

            bool same = OutputCard.IsSameAsSource;
            if (!same && OutputCard.Status.IsError)
            {
                Fail(T("Str_MetaClean_CantSave", "Can't save there: ") + OutputCard.Status.Text);
                return;
            }
            string? folder;
            try
            {
                folder = same ? null : OutputCard.EnsureFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Fail(T("Str_MetaClean_CantSave", "Can't save there: ") + ex.Message);
                return;
            }

            foreach (var item in _items) item.Reset();
            PillClean.Visibility = Visibility.Collapsed;
            ProgressRun.Value = 0;
            TxtProgress.Text = T("Str_MetaClean_Working", "Cleaning…");
            TxtProgressDetail.Text = "";
            _runCts = new CancellationTokenSource();
            SetBusy(true);

            var progress = new Progress<CleanProgress>(p =>
            {
                ProgressRun.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                TxtProgress.Text = string.Format(T("Str_MetaClean_Progress", "{0} of {1} done"), p.Done, p.Total);
            });

            bool canceled;
            try
            {
                await _cleaner.RunAsync(_items.ToList(), _settings.Mode, _settings.Suffix, folder, same, progress, _runCts.Token);
                canceled = _runCts.IsCancellationRequested;
            }
            finally
            {
                SetBusy(false);
            }
            await Task.Delay(150);   // let the last queued progress and item updates land
            Summarize(canceled);
        }

        private void Summarize(bool canceled)
        {
            int done = _items.Count(i => i.Status == CleanStatus.Done), failed = _items.Count(i => i.Status == CleanStatus.Failed), skipped = _items.Count(i => i.Status == CleanStatus.Skipped);
            ProgressRun.Value = _items.Count == 0 ? 0 : (done + failed + skipped) * 100.0 / _items.Count;

            string head = canceled ? T("Str_MetaClean_Canceled", "Canceled. What was finished is kept.") : T("Str_MetaClean_Finished", "Finished.");
            TxtProgress.Text = $"{head} " + string.Format(T("Str_MetaClean_Summary", "{0} cleaned, {1} skipped, {2} failed."), done, skipped, failed);
            TxtProgressDetail.Text = failed + skipped > 0 ? T("Str_MetaClean_SeeRows", "Point at a status to see why.") : "";

            if (done > 0)
            {
                int locations = _items.Count(i => i.Status == CleanStatus.Done && i.HasLocation);
                TxtClean.Text = locations > 0
                    ? string.Format(T("Str_MetaClean_LocationsRemoved", "{0} locations removed"), locations)
                    : string.Format(T("Str_MetaClean_CleanCopies", "{0} clean copies"), done);
                PillClean.Visibility = Visibility.Visible;
                UiMotion.Pop(PillClean);
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            OptionsScrollViewer.IsEnabled = !busy;
            InputCard.IsLocked = busy;
            OutputCard.IsLocked = busy;
            IconRun.Kind = busy ? PackIconKind.Close : PackIconKind.EyeOffOutline;
            TxtRunLabel.SetResourceReference(TextBlock.TextProperty, busy ? "Str_MetaClean_Cancel" : "Str_MetaClean_Run");
            BtnRun.IsEnabled = busy || _items.Count > 0;
        }

        private void Fail(string message)
        {
            TxtProgress.Text = message;
            TxtProgressDetail.Text = "";
            ThemedMessageBox.Show(message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>The status of a picture as a word, in the current language.</summary>
    public sealed class CleanStatusTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string name = value is CleanStatus s ? s.ToString() : "Pending";
            return Application.Current?.TryFindResource("Str_MetaClean_Status" + name) as string ?? name;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
