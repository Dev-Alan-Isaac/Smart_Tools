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
using System.Windows.Media;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Bulk Image Resizer - UI only. Finding pictures, resizing, and the settings file are in
    /// <see cref="BulkImageResizerManager"/>. Originals are never changed; every result is a new file.
    /// </summary>
    public partial class BulkImageResizerView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;
        private const int ThumbnailLimit = 300;   // thumbnails for the first pictures only: 10,000 of them would be slow and heavy
        private const string DialogTitle = "Bulk Image Resizer";

        private readonly BulkImageResizerManager _resizer = new();
        private readonly ObservableCollection<BulkImageItem> _items = new();
        private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _runCts, _thumbCts;
        private BulkResizeSettings _settings = new();
        private bool _viewConstructed, _loadedOnce, _applying, _busy, _optionsCollapsed;

        public BulkImageResizerView()
        {
            InitializeComponent();
            GridItems.ItemsSource = _items;
            InputCard.ItemsSource = _items;
            InputCard.FileFilterProvider = () => T("Str_BulkResize_FilterPictures", "Pictures") + "|*.jpg;*.jpeg;*.jpe;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.heic;*.heif;*.avif;*.svg|" + T("Str_BulkResize_FilterAll", "All files") + "|*.*";
            _items.CollectionChanged += (_, _) => UpdateEmptyAndCount();
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a run (finished pictures stay) and the thumbnail loader.</summary>
        public void CancelActiveRun()
        {
            _runCts?.Cancel();
            _thumbCts?.Cancel();
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _settings = BulkImageResizerManager.LoadSettings();
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

        private void ApplySettings(BulkResizeSettings s)
        {
            _applying = true;
            try
            {
                foreach (var r in ModeButtons) r.IsChecked = (string)r.Tag == s.Mode.ToString();
                TxtPercent.Text = s.Percent.ToString(CultureInfo.InvariantCulture);
                TxtWidth.Text = s.Width.ToString(CultureInfo.InvariantCulture);
                TxtHeight.Text = s.Height.ToString(CultureInfo.InvariantCulture);
                ChkOnlyShrink.IsChecked = s.OnlyShrink;
                SliderQuality.Value = Math.Clamp(s.Quality, (int)SliderQuality.Minimum, (int)SliderQuality.Maximum);
                TxtQuality.Text = ((int)SliderQuality.Value).ToString(CultureInfo.CurrentCulture);
                ChkTarget.IsChecked = s.TargetEnabled;
                TxtTargetKb.Text = s.TargetKb.ToString(CultureInfo.InvariantCulture);
                ChkStrip.IsChecked = s.StripMetadata;
                TxtSuffix.Text = s.Suffix;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                foreach (var r in FormatButtons()) r.IsChecked = (string)r.Tag == s.Format.ToString();
            }
            finally
            {
                _applying = false;
            }
            UpdateModeFields();
            UpdateFormatState();
        }

        private RadioButton[] ModeButtons => new[] { ModePercent, ModeFit, ModeWidth, ModeHeight };

        private RadioButton[] FormatButtons() => new[] { FormatKeep, FormatJpg, FormatPng, FormatWebP };

        /// <summary>Reads the controls. A box that doesn't hold a valid number keeps its last good value.</summary>
        private BulkResizeSettings ReadSettings()
        {
            var s = new BulkResizeSettings
            {
                Mode = Enum.TryParse(ModeButtons.FirstOrDefault(r => r.IsChecked == true)?.Tag as string, out BulkResizeMode m) ? m : _settings.Mode,
                Percent = Number(TxtPercent, _settings.Percent),
                Width = Number(TxtWidth, _settings.Width),
                Height = Number(TxtHeight, _settings.Height),
                OnlyShrink = ChkOnlyShrink.IsChecked == true,
                Quality = (int)SliderQuality.Value,
                TargetEnabled = ChkTarget.IsChecked == true,
                TargetKb = Number(TxtTargetKb, _settings.TargetKb),
                StripMetadata = ChkStrip.IsChecked == true,
                Suffix = TxtSuffix.Text,
                IncludeSubfolders = InputCard.IncludeSubfolders,
                Format = Enum.TryParse(FormatButtons().FirstOrDefault(r => r.IsChecked == true)?.Tag as string, out ResizeFormat f) ? f : _settings.Format,
            };
            return BulkImageResizerManager.Sanitize(s);

            static int Number(TextBox box, int fallback) => int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : fallback;
        }

        private void SaveSettings()
        {
            if (!_viewConstructed || _applying) return;
            _settings = ReadSettings();
            // Show what was actually accepted (a typed 9999 percent becomes 400, text becomes the last good number).
            _applying = true;
            TxtPercent.Text = _settings.Percent.ToString(CultureInfo.InvariantCulture);
            TxtWidth.Text = _settings.Width.ToString(CultureInfo.InvariantCulture);
            TxtHeight.Text = _settings.Height.ToString(CultureInfo.InvariantCulture);
            TxtTargetKb.Text = _settings.TargetKb.ToString(CultureInfo.InvariantCulture);
            TxtSuffix.Text = _settings.Suffix;
            _applying = false;
            BulkImageResizerManager.SaveSettings(_settings);
        }

        private void Field_LostFocus(object sender, RoutedEventArgs e) => SaveSettings();

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            UpdateModeFields();
        }

        private void Format_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            UpdateFormatState();
        }

        private void SliderQuality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtQuality.Text = ((int)e.NewValue).ToString(CultureInfo.CurrentCulture);
            SaveSettings();
        }

        private void UpdateModeFields()
        {
            var mode = _settings.Mode;
            FieldPercent.Visibility = mode == BulkResizeMode.Percent ? Visibility.Visible : Visibility.Collapsed;
            FieldSize.Visibility = mode == BulkResizeMode.Percent ? Visibility.Collapsed : Visibility.Visible;
            FieldWidth.Visibility = mode == BulkResizeMode.Height ? Visibility.Collapsed : Visibility.Visible;
            FieldHeight.Visibility = mode == BulkResizeMode.Width ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumnSpan(FieldWidth, mode == BulkResizeMode.Width ? 3 : 1);
            Grid.SetColumn(FieldHeight, mode == BulkResizeMode.Height ? 0 : 2);
            Grid.SetColumnSpan(FieldHeight, mode == BulkResizeMode.Height ? 3 : 1);
            TxtSizeHint.Text = mode switch
            {
                BulkResizeMode.Percent => T("Str_BulkResize_HintPercent", "100% keeps the size. Up to 400%."),
                BulkResizeMode.FitWithin => T("Str_BulkResize_HintFit", "Each picture fits inside this box and keeps its proportions. 0 = no limit on that side."),
                BulkResizeMode.Width => T("Str_BulkResize_HintWidth", "Every picture gets this width; the height follows."),
                _ => T("Str_BulkResize_HintHeight", "Every picture gets this height; the width follows."),
            };
        }

        /// <summary>PNG has no quality or size trade-off, so those controls are off while it is chosen.</summary>
        private void UpdateFormatState()
        {
            bool lossless = _settings.Format == ResizeFormat.Png;
            SliderQuality.IsEnabled = !lossless;
            ChkTarget.IsEnabled = !lossless;
        }

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
            _thumbCts?.Cancel();
            _items.Clear();
            _known.Clear();
            ShowIdle();
        }

        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            bool include = InputCard.IncludeSubfolders;
            TxtProgress.Text = T("Str_BulkResize_Reading", "Looking for pictures…");
            TxtProgressDetail.Text = "";

            var added = await Task.Run(() =>
            {
                var files = BulkImageResizerManager.ScanPictures(paths, include, CancellationToken.None);
                var created = new List<BulkImageItem>();
                foreach (string file in files)
                {
                    try
                    {
                        var item = new BulkImageItem { Path = file, Name = Path.GetFileName(file), OriginalBytes = new FileInfo(file).Length };
                        BulkImageResizerManager.ReadSize(item);
                        created.Add(item);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file that vanished or can't be read is simply not added.
                    }
                }
                return created;
            });

            int skippedDuplicates = 0;
            var fresh = new List<BulkImageItem>();
            foreach (var item in added)
            {
                if (_known.Add(item.Path)) { _items.Add(item); fresh.Add(item); }
                else skippedDuplicates++;
            }

            TxtProgress.Text = fresh.Count == 0
                ? T("Str_BulkResize_NoneAdded", "No new pictures were added.")
                : string.Format(T("Str_BulkResize_Added", "Added {0} pictures."), fresh.Count);
            TxtProgressDetail.Text = skippedDuplicates > 0 ? string.Format(T("Str_BulkResize_Duplicates", "{0} were already in the list."), skippedDuplicates) : "";
            _ = LoadThumbnailsAsync(fresh);
        }

        private async Task LoadThumbnailsAsync(List<BulkImageItem> items)
        {
            _thumbCts?.Cancel();
            _thumbCts = new CancellationTokenSource();
            var token = _thumbCts.Token;
            try
            {
                foreach (var item in items.Take(ThumbnailLimit))
                {
                    token.ThrowIfCancellationRequested();
                    var thumb = await ThumbnailService.GetThumbnailAsync(item.Path, 80, token);
                    if (thumb != null) item.Thumbnail = thumb;
                }
            }
            catch (OperationCanceledException)
            {
                // A newer batch of pictures took over, or the app is closing.
            }
        }

        private void UpdateEmptyAndCount()
        {
            PanelEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            InputCard.SetEmpty(_items.Count == 0);
            TxtCount.Text = string.Format(T("Str_BulkResize_Count", "{0} pictures"), _items.Count);
            if (!_busy) BtnRun.IsEnabled = _items.Count > 0;
        }

        private void ShowIdle()
        {
            TxtProgress.Text = T("Str_BulkResize_Idle", "Add pictures, choose a size, and press Resize.");
            TxtProgressDetail.Text = T("Str_BulkResize_IdleDetail", "Your originals are never changed. Results are saved as new files.");
            ProgressRun.Value = 0;
            PillSaved.Visibility = Visibility.Collapsed;
        }

        private void BtnRemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || (sender as FrameworkElement)?.DataContext is not BulkImageItem item) return;
            _items.Remove(item);
            _known.Remove(item.Path);
        }

        private void BtnShowResult_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not BulkImageItem { NewPath.Length: > 0 } item || !File.Exists(item.NewPath)) return;
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
            var options = _settings.ToOptions();

            bool same = OutputCard.IsSameAsSource;
            if (!same && OutputCard.Status.IsError)
            {
                Fail(T("Str_BulkResize_CantSave", "Can't save there: ") + OutputCard.Status.Text);
                return;
            }
            string? folder;
            try
            {
                folder = same ? null : OutputCard.EnsureFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Fail(T("Str_BulkResize_CantSave", "Can't save there: ") + ex.Message);
                return;
            }

            foreach (var item in _items) item.Reset();
            PillSaved.Visibility = Visibility.Collapsed;
            ProgressRun.Value = 0;
            TxtProgress.Text = T("Str_BulkResize_Working", "Resizing…");
            TxtProgressDetail.Text = "";
            _runCts = new CancellationTokenSource();
            SetBusy(true);

            ResizeProgress? last = null;
            var progress = new Progress<ResizeProgress>(p =>
            {
                last = p;
                ProgressRun.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                TxtProgress.Text = string.Format(T("Str_BulkResize_Progress", "{0} of {1} done"), p.Done, p.Total);
                TxtProgressDetail.Text = p.BytesBefore > 0 ? string.Format(T("Str_BulkResize_ProgressSizes", "{0} → {1} so far"), OutputFolderManager.FormatBytes(p.BytesBefore), OutputFolderManager.FormatBytes(p.BytesAfter)) : "";
            });

            bool canceled;
            try
            {
                await _resizer.RunAsync(_items.ToList(), options, folder, same, progress, _runCts.Token);
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
            int done = _items.Count(i => i.Status == ResizeStatus.Done), failed = _items.Count(i => i.Status == ResizeStatus.Failed), skipped = _items.Count(i => i.Status == ResizeStatus.Skipped);
            long before = _items.Where(i => i.Status == ResizeStatus.Done).Sum(i => i.OriginalBytes), after = _items.Where(i => i.Status == ResizeStatus.Done).Sum(i => i.NewBytes);
            ProgressRun.Value = _items.Count == 0 ? 0 : (done + failed + skipped) * 100.0 / _items.Count;

            string head = canceled ? T("Str_BulkResize_Canceled", "Canceled. What was finished is kept.") : T("Str_BulkResize_Finished", "Finished.");
            TxtProgress.Text = $"{head} " + string.Format(T("Str_BulkResize_Summary", "{0} resized, {1} skipped, {2} failed."), done, skipped, failed);

            if (done > 0 && before > 0)
            {
                long saved = before - after;
                TxtSaved.Text = saved >= 0
                    ? string.Format(T("Str_BulkResize_Saved", "Saved {0}"), OutputFolderManager.FormatBytes(saved))
                    : string.Format(T("Str_BulkResize_Larger", "{0} larger"), OutputFolderManager.FormatBytes(-saved));
                TxtProgressDetail.Text = string.Format(T("Str_BulkResize_ProgressSizes", "{0} → {1} so far"), OutputFolderManager.FormatBytes(before), OutputFolderManager.FormatBytes(after));
                PillSaved.Visibility = Visibility.Visible;
                UiMotion.Pop(PillSaved);
            }
            else
            {
                TxtProgressDetail.Text = failed > 0 ? T("Str_BulkResize_SeeRows", "Point at a status to see why.") : "";
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            OptionsScrollViewer.IsEnabled = !busy;
            InputCard.IsLocked = busy;
            OutputCard.IsLocked = busy;
            IconRun.Kind = busy ? PackIconKind.Close : PackIconKind.ResizeBottomRight;
            TxtRunLabel.SetResourceReference(TextBlock.TextProperty, busy ? "Str_BulkResize_Cancel" : "Str_BulkResize_Run");
            BtnRun.IsEnabled = busy || _items.Count > 0;
        }

        private void Fail(string message)
        {
            TxtProgress.Text = message;
            TxtProgressDetail.Text = "";
            MessageBox.Show(message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- Small converters for the results grid ----

    /// <summary>A byte count as "1.4 MB". Empty for 0, so a picture that isn't done yet shows no "after" size.</summary>
    public sealed class BytesConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is long bytes && bytes > 0 ? OutputFolderManager.FormatBytes(bytes) : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>The saved share: "42%" smaller, "+5%" when the result is bigger, empty while there is no result.</summary>
    public sealed class SavedPercentConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            int p and > 0 => $"−{p}%",
            int p and < 0 => $"+{-p}%",
            _ => "",
        };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class ResizeStatusTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string name = value is ResizeStatus s ? s.ToString() : "Pending";
            return Application.Current?.TryFindResource("Str_BulkResize_Status" + name) as string ?? name;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>The model keeps the thumbnail as a plain object; the grid needs it as a picture.</summary>
    public sealed class ThumbnailConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value as ImageSource;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
