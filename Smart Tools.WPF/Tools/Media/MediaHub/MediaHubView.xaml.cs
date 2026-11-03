using Smart_Tools.WPF.Controls;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Media Viewer: one tool, four pages. Mixed is the original Media Viewer (pictures, videos and music in one queue);
    /// Photos, Videos and Music are the Image Viewer, the VLC video player and the audio player with its visualizer,
    /// unchanged. This view only switches between them and sends dropped files to the page that fits.
    /// </summary>
    public partial class MediaHubView : UserControl
    {
        private enum Page { Mixed, Photos, Videos, Music }

        private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".heif", ".avif", ".jfif" };

        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".flv", ".ts", ".mts", ".m2ts", ".3gp", ".vob", ".ogv" };

        private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".aifc", ".mid", ".midi" };

        private static Page _lastPage = Page.Mixed;   // remembered while the app runs

        private readonly Dictionary<Page, UIElement> _pages = new();
        private Page _current = Page.Mixed;
        private bool _switching;

        public MediaHubView()
        {
            InitializeComponent();
            Show(_lastPage);
        }

        // =================================================================
        //  Switching pages
        // =================================================================

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_switching || sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out Page page)) return;
            Show(page);
        }

        private UIElement GetPage(Page page)
        {
            if (!_pages.TryGetValue(page, out var view))
            {
                view = page switch
                {
                    Page.Photos => new ImageViewerControl(),
                    Page.Videos => new VideoReplayView(),
                    Page.Music => new AudioReplayControl(),
                    _ => new MediaViewerView(),
                };
                _pages[page] = view;
            }
            return view;
        }

        private void Show(Page page)
        {
            _switching = true;
            (page switch
            {
                Page.Photos => RadioPhotos,
                Page.Videos => RadioVideos,
                Page.Music => RadioMusic,
                _ => RadioMixed,
            }).IsChecked = true;
            _switching = false;

            _current = page;
            _lastPage = page;
            // Swapping the content unloads the old page (it stops its own playback, like leaving any tool) and loads the new one.
            PageHost.Content = GetPage(page);
            TxtHint.Text = page switch
            {
                Page.Photos => "Pictures only. Drop videos or music anywhere to jump to the right page.",
                Page.Videos => "Videos only. Drop pictures or music anywhere to jump to the right page.",
                Page.Music => "Music only. Drop pictures or videos anywhere to jump to the right page.",
                _ => "Everything in one queue. Use Photos, Videos or Music for the full tools.",
            };
        }

        // =================================================================
        //  Drops: files go to the page that fits their type
        // =================================================================

        /// <summary>The page a drop belongs on, or null when it should stay where it is (folders, unknown types, or the Mixed page).</summary>
        private Page? TargetFor(string[] paths)
        {
            if (_current == Page.Mixed || paths.Length == 0) return null;
            bool image = false, video = false, audio = false;
            foreach (string p in paths)
            {
                if (Directory.Exists(p)) return null;   // a folder: let the current page decide what is in it
                string ext = Path.GetExtension(p);
                if (ImageExt.Contains(ext)) image = true;
                else if (VideoExt.Contains(ext)) video = true;
                else if (AudioExt.Contains(ext)) audio = true;
            }
            int kinds = (image ? 1 : 0) + (video ? 1 : 0) + (audio ? 1 : 0);
            if (kinds == 0) return null;
            Page target = kinds > 1 ? Page.Mixed : image ? Page.Photos : video ? Page.Videos : Page.Music;
            return target == _current ? null : target;
        }

        private void Hub_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            if (TargetFor(paths) == null) return;
            e.Effects = DragDropEffects.Copy;   // the current page might refuse these files; the drop will move them
            e.Handled = true;
        }

        private void Hub_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            if (TargetFor(paths) is not Page target) return;

            Show(target);
            switch (GetPage(target))
            {
                case MediaViewerView m: m.AddPaths(paths); break;
                case ImageViewerControl i: i.AddPaths(paths); break;
                case VideoReplayView v: v.AddPaths(paths); break;
                case AudioReplayControl a: a.AddPaths(paths); break;
            }
            e.Handled = true;
        }

        // =================================================================
        //  Closing
        // =================================================================

        /// <summary>Called from MainWindow.Window_Closing: lets every page that was opened release its files and devices.</summary>
        public void CancelActiveRun()
        {
            foreach (var view in _pages.Values)
            {
                switch (view)
                {
                    case MediaViewerView m: m.CancelActiveRun(); break;
                    case ImageViewerControl i: i.CancelActiveRun(); break;
                    case VideoReplayView v: v.CancelActiveRun(); break;
                    case AudioReplayControl a: a.ReleaseAudio(); break;
                }
            }
        }
    }
}
