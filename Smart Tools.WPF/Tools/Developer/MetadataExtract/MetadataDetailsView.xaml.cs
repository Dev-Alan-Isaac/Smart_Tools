using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Engines;
using Smart_Tools.WPF.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// What the Metadata Extractor knows about one file, in a window over the list: its picture and basics on the left,
    /// everything found on the right, and previous / next to walk the list (wrapping round, like Find Similar).
    /// It only reads what the list already holds; the list stays behind it and picks up the file you ended on.
    /// </summary>
    public partial class MetadataDetailsView
    {
        private const int PictureSize = 640;

        private readonly IReadOnlyList<MetadataFileItem> _items;
        private readonly Action _close;
        private int _index;
        private MetadataFileItem? _watched;
        private ListCollectionView? _view;
        private CancellationTokenSource? _pictureCts;

        public MetadataDetailsView(IReadOnlyList<MetadataFileItem> items, int index, Action close)
        {
            InitializeComponent();
            _items = items;
            _close = close;
            AppModal.CancelOnEscape(this, close);
            GoTo(index);
        }

        /// <summary>The file on show (the list selects it again when this closes).</summary>
        public MetadataFileItem? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

        // =================================================================
        //  Moving between files
        // =================================================================

        private void GoTo(int index)
        {
            if (_watched != null) _watched.PropertyChanged -= Watched_PropertyChanged;
            _index = index;
            _watched = Current;
            if (_watched != null) _watched.PropertyChanged += Watched_PropertyChanged;
            TxtNote.Text = "";
            Render();
            LoadPicture(_watched);
        }

        private void Step(int delta) => GoTo(MetadataDetailsNavigation.Step(_items.Count, _index, delta));

        private void BtnPrev_Click(object sender, RoutedEventArgs e) => Step(-1);

        private void BtnNext_Click(object sender, RoutedEventArgs e) => Step(+1);

        private void BtnClose_Click(object sender, RoutedEventArgs e) => _close();

        /// <summary>Arrow keys and Page Up / Down walk the list - except while typing in the filter, where the arrows move the caret.</summary>
        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool typing = Keyboard.FocusedElement is TextBox;
            switch (e.Key)
            {
                case Key.Left when !typing:
                case Key.PageUp:
                    Step(-1);
                    e.Handled = true;
                    break;
                case Key.Right when !typing:
                case Key.PageDown:
                    Step(+1);
                    e.Handled = true;
                    break;
            }
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_watched != null) _watched.PropertyChanged -= Watched_PropertyChanged;
            _watched = null;
            _pictureCts?.Cancel();
        }

        /// <summary>A file that is still being read fills in live.</summary>
        private void Watched_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MetadataFileItem.Fields) or nameof(MetadataFileItem.Status) or nameof(MetadataFileItem.Message) or nameof(MetadataFileItem.HasLocation))
                Render();
        }

        // =================================================================
        //  Showing a file
        // =================================================================

        private void Render()
        {
            var item = Current;
            if (item == null)
            {
                _close();
                return;
            }

            var kind = item.Kind switch
            {
                MetadataKind.Audio => PackIconKind.MusicNote,
                MetadataKind.Image => PackIconKind.ImageOutline,
                _ => PackIconKind.FilmstripBox,
            };
            KindIcon.Kind = ThumbIcon.Kind = kind;
            TxtName.Text = item.Name;
            TxtSub.Text = item.Summary.Length > 0 ? item.Summary : $"{item.Kind} · {item.Format} · {item.SizeText}";
            TxtFactsMain.Text = $"{item.Kind} · {item.Format} · {item.SizeText}";
            TxtFactsDate.Text = item.Modified.ToString("g");
            TxtFactsFolder.Text = item.Folder;

            TxtPosition.Text = MetadataDetailsNavigation.Position(_items.Count, _index);
            BtnPrev.IsEnabled = BtnNext.IsEnabled = MetadataDetailsNavigation.CanStep(_items.Count);
            BtnOpenMap.IsEnabled = item.HasLocation;

            bool failed = item.Status == MetadataStatus.Failed;
            ErrorBanner.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            TxtError.Text = failed ? item.Message : "";

            ShowFields(item.Fields);
            if (item.Fields is not { Count: > 0 }) TxtEmpty.Text = EmptyText(item);
        }

        private static string EmptyText(MetadataFileItem item) => item.Status switch
        {
            MetadataStatus.Working => Loc.T("Str_MetadataDetails_Reading", "Reading…"),
            MetadataStatus.Skipped => Loc.F("Str_MetadataDetails_SkippedHint", "{0}. Switch it on in the sidebar and click Extract.", item.Message),
            MetadataStatus.Failed => Loc.T("Str_MetadataDetails_NothingRead", "Nothing could be read from this file."),
            _ => Loc.T("Str_MetadataDetails_NotRead", "Not read yet - click Extract."),
        };

        private void ShowFields(IReadOnlyList<MetadataField>? fields)
        {
            bool any = fields is { Count: > 0 };
            if (any)
            {
                _view = new ListCollectionView(fields!.ToList());
                _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MetadataField.Group)));
                _view.Filter = MatchesFilter;
            }
            else
            {
                _view = null;
            }
            ItemsCards.ItemsSource = _view;
            ScrollCards.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            FilterBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            EmptyPanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            BtnCopyAll.IsEnabled = any;
            if (any) UiMotion.FadeIn(ScrollCards, 4);
        }

        private bool MatchesFilter(object o)
        {
            string term = TxtFilter.Text.Trim();
            return term.Length == 0 || o is MetadataField f &&
                   (f.Property.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                    f.Value.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                    f.Group.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        private void TxtFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();

        // =================================================================
        //  The picture
        // =================================================================

        /// <summary>Loads off the UI thread; stepping to another file cancels the one still loading. Audio (and anything unreadable) keeps the kind icon.</summary>
        private async void LoadPicture(MetadataFileItem? item)
        {
            _pictureCts?.Cancel();
            ThumbImage.Source = null;
            ThumbIcon.Visibility = Visibility.Visible;
            TxtThumbState.Visibility = Visibility.Collapsed;
            if (item == null || !ThumbnailService.SupportsThumbnail(item.FullPath)) return;

            var cts = _pictureCts = new CancellationTokenSource();
            TxtThumbState.Visibility = Visibility.Visible;
            try
            {
                var picture = await ThumbnailService.GetThumbnailAsync(item.FullPath, PictureSize, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (picture != null)
                {
                    ThumbImage.Source = picture;
                    ThumbIcon.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException)
            {
                return;   // another file was picked meanwhile
            }
            if (!cts.IsCancellationRequested) TxtThumbState.Visibility = Visibility.Collapsed;
        }

        // =================================================================
        //  Actions
        // =================================================================

        private void Note(string text) => TxtNote.Text = text;

        private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (Current is not { } item) return;
            if (!File.Exists(item.FullPath)) Note(Loc.F("Str_MetadataDetails_NoLongerThere", "{0} is no longer there.", item.Name));
            else if (!ShellOpen.OpenFile(item.FullPath)) Note(Loc.F("Str_MetadataDetails_CouldntOpen", "Couldn't open {0}.", item.Name));
        }

        private void BtnOpenMap_Click(object sender, RoutedEventArgs e)
        {
            if (Current is not { Latitude: { } lat, Longitude: { } lon }) return;
            if (!ShellOpen.OpenUrl(MetadataExtractManager.MapUrl(lat, lon)))
                Note(Loc.T("Str_MetadataDetails_CouldntOpenBrowser", "Couldn't open the browser."));
        }

        private void BtnShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Current is not { } item) return;
            try
            {
                if (File.Exists(item.FullPath)) Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
                else Note(Loc.F("Str_MetadataDetails_NoLongerThere", "{0} is no longer there.", item.Name));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Note(Loc.F("Str_MetadataDetails_CouldntOpenFolder", "Couldn't open the folder: {0}", ex.Message));
            }
        }

        /// <summary>The whole list as text - only what the filter leaves, like what is on screen.</summary>
        private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
        {
            if (Current is not { } item || _view == null) return;
            var sb = new StringBuilder().AppendLine(item.FullPath);
            foreach (var group in _view.Cast<MetadataField>().GroupBy(f => f.Group))
            {
                sb.AppendLine().AppendLine(group.Key);
                foreach (var f in group) sb.Append("  ").Append(f.Property).Append(": ").AppendLine(f.Value);
            }
            if (TryCopy(sb.ToString())) Note(Loc.F("Str_MetadataDetails_CopiedAll", "Copied the details of {0}.", item.Name));
            else Note(Loc.T("Str_MetadataExtract_CopyBusy", "Another program has the clipboard. Try again."));
        }

        /// <summary>A click on a tile copies that one value.</summary>
        private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not MetadataField field) return;
            if (TryCopy(field.Value)) Note(Loc.F("Str_MetadataExtract_CopiedValue", "Copied {0}.", field.Property));
            else Note(Loc.T("Str_MetadataExtract_CopyBusy", "Another program has the clipboard. Try again."));
        }

        /// <summary>Another program briefly holding the clipboard is normal: try a few times.</summary>
        private static bool TryCopy(string text)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch (COMException)
                {
                    Thread.Sleep(60);
                }
            }
            return false;
        }
    }
}
