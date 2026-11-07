using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using ListBox = System.Windows.Controls.ListBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Clipboard History - UI only. Listening, the list and putting items back are in <see cref="ClipboardHistoryManager"/>.</summary>
    public partial class ClipboardHistoryView : UserControl
    {
        private readonly ClipboardHistoryManager _history = ClipboardHistoryService.Instance;
        private readonly bool _viewConstructed;
        private bool _loadedOnce;

        public ClipboardHistoryView()
        {
            InitializeComponent();
            _history.Changed += RefreshList;
            ClipboardWidgetManager.Changed += UpdateWidgetIcon;
            _viewConstructed = true;
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ClipboardHistoryService.ToolOpened();
            UpdateRecordingUi();
            RefreshList();
        }

        // =================================================================
        //  List
        // =================================================================

        private void RefreshList()
        {
            string day = (CmbDays.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
            var items = _history.Snapshot(TxtSearch.Text).Where(i => InDayRange(i, day)).ToList();
            foreach (var item in items)
            {
                item.MetaText = MetaOf(item);
                item.DayGroup = DayGroupOf(item);
            }
            var view = new ListCollectionView(items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClipboardItem.DayGroup)));   // the list is already pinned first, then newest first
            ListItems.ItemsSource = view;

            TxtCount.Text = string.Format(T("Str_Clipboard_Count", "{0} items"), _history.Count);
            PanelEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool searching = _history.Count > 0;
            TxtEmptyTitle.Text = searching ? T("Str_Clipboard_NoMatches", "Nothing matches") : T("Str_Clipboard_EmptyTitle", "Nothing copied yet");
            TxtEmptyHint.Text = searching
                ? T("Str_Clipboard_NoMatchesHint", "Try another search. Pictures can't be searched.")
                : T("Str_Clipboard_EmptyHint", "Copy some text or a picture and it appears here. History starts when you open this tool, stays in memory only, and is gone when Smart Tools closes. Copies marked private, like the Password Vault's, are never recorded.");
        }

        private static bool InDayRange(ClipboardItem item, string range)
        {
            var today = DateTime.Today;
            var date = item.CopiedAt.Date;
            return range switch
            {
                "today" => date == today,
                "yesterday" => date == today.AddDays(-1),
                "week" => date > today.AddDays(-7),
                "month" => date > today.AddDays(-30),
                "older" => date <= today.AddDays(-30),
                _ => true,
            };
        }

        /// <summary>Pinned items sit under their own heading; the rest under Today, Yesterday, or the day's name and date.</summary>
        private static string DayGroupOf(ClipboardItem item)
        {
            if (item.IsPinned) return T("Str_Clipboard_GroupPinned", "Pinned");
            var today = DateTime.Today;
            var date = item.CopiedAt.Date;
            if (date == today) return T("Str_Clipboard_GroupToday", "Today");
            if (date == today.AddDays(-1)) return T("Str_Clipboard_GroupYesterday", "Yesterday");
            return date > today.AddDays(-7)
                ? date.ToString("dddd, d MMMM", CultureInfo.CurrentCulture)
                : date.ToString("D", CultureInfo.CurrentCulture);
        }

        private void BtnWidget_Click(object sender, RoutedEventArgs e) => ClipboardWidgetManager.Toggle();

        private void UpdateWidgetIcon()
        {
            IconWidget.Kind = ClipboardWidgetManager.IsOpen ? PackIconKind.PictureInPictureBottomRightOutline : PackIconKind.PictureInPictureBottomRight;
            if (ClipboardWidgetManager.IsOpen) BtnWidget.SetResourceReference(BackgroundProperty, "AccentColorSubtle");
            else BtnWidget.ClearValue(BackgroundProperty);
        }

        private void CmbDays_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewConstructed && _loadedOnce) RefreshList();
        }

        private static string MetaOf(ClipboardItem item)
        {
            string when = item.CopiedAt.Date == DateTime.Today ? item.CopiedAt.ToString("t", CultureInfo.CurrentCulture) : item.CopiedAt.ToString("g", CultureInfo.CurrentCulture);
            return item.IsImage
                ? string.Format(T("Str_Clipboard_MetaImage", "{0} · picture"), when)
                : string.Format(T("Str_Clipboard_MetaText", "{0} · {1} characters"), when, item.Text.Length.ToString("N0", CultureInfo.CurrentCulture));
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed && _loadedOnce) RefreshList();
        }

        // =================================================================
        //  Item actions
        // =================================================================

        private static ClipboardItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipboardItem;

        private void BtnPin_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) _history.TogglePin(item);
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) _history.Remove(item);
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) CopyItem(item);
        }

        private void ListItems_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ListItems, source) is ListBoxItem { DataContext: ClipboardItem item })
                CopyItem(item);
        }

        private void CopyItem(ClipboardItem item)
        {
            bool ok = _history.CopyBack(item);
            SetStatus(ok ? T("Str_Clipboard_Copied", "Copied to the clipboard.") : T("Str_Clipboard_CopyFailed", "Couldn't copy - another program is using the clipboard."), ok ? "TextSecondary" : "StatusErrorFg");
        }

        // =================================================================
        //  Pause, clear
        // =================================================================

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            _history.IsRecording = !_history.IsRecording;
            UpdateRecordingUi();
        }

        private void UpdateRecordingUi()
        {
            bool on = _history.IsRecording;
            TxtState.Text = on ? T("Str_Clipboard_Recording", "Recording") : T("Str_Clipboard_Paused", "Paused");
            PillState.SetResourceReference(Border.BackgroundProperty, on ? "StatusSuccessBg" : "StatusWarningBg");
            IconState.SetResourceReference(PackIcon.ForegroundProperty, on ? "StatusSuccessFg" : "StatusWarningFg");
            TxtState.SetResourceReference(TextBlock.ForegroundProperty, on ? "StatusSuccessFg" : "StatusWarningFg");
            IconPause.Kind = on ? PackIconKind.PauseCircleOutline : PackIconKind.PlayCircleOutline;
            BtnPause.SetResourceReference(ToolTipProperty, on ? "Str_Clipboard_PauseTip" : "Str_Clipboard_ResumeTip");
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            int removed = _history.ClearUnpinned();
            SetStatus(string.Format(T("Str_Clipboard_Cleared", "Cleared {0} items. Pinned items were kept."), removed), "TextSecondary");
        }

        private void SetStatus(string text, string colorKey)
        {
            TxtStatus.Text = text;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            TxtStatus.Visibility = Visibility.Visible;
        }
    }

    /// <summary>PNG bytes -> a small picture for the list (decoded at thumbnail size, so a big screenshot doesn't cost memory per row).</summary>
    public sealed class PngToImageConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not byte[] { Length: > 0 } png) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 96;
                image.StreamSource = new MemoryStream(png);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException)
            {
                return null;
            }
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
