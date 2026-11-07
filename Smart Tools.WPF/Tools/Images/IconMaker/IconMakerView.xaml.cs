using Smart_Tools.WPF.Controllers;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using System.Windows.Threading;
using CheckBox = System.Windows.Controls.CheckBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Icon Maker - UI only. Resizing, the .ico and the favicon set are <see cref="IconMakerManager"/>'s.</summary>
    public partial class IconMakerView : UserControl
    {
        public sealed record IconPreview(int Size, int Display, int Box, string Label, string Note, BitmapSource Picture);

        private readonly List<(int Size, BitmapSource Picture)> _pictures = new();
        private double _zoom = 1;

        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp" };

        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _doneTimer;
        private readonly bool _viewConstructed;
        private bool _loadedOnce;
        private bool _applying;
        private byte[]? _bgra;
        private int _width, _height;
        private string _name = "";

        public IconMakerView()
        {
            InitializeComponent();
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); DonePill.Visibility = Visibility.Collapsed; };
            _viewConstructed = true;
        }

        // =================================================================
        //  Settings
        // =================================================================

        private IEnumerable<CheckBox> SizeBoxes => new[] { Size16, Size24, Size32, Size48, Size64, Size128, Size256 };

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            var s = IconMakerManager.Clamp(JsonSettings.Load<IconMakerSettings>(IconMakerManager.SettingsName));
            _applying = true;
            (s.Fit == IconFit.Fill ? RadioFill : RadioFit).IsChecked = true;
            foreach (var box in SizeBoxes) box.IsChecked = s.Sizes.Contains(int.Parse((string)box.Tag));
            _applying = false;
            UpdateHint();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private IconFit Fit => RadioFill.IsChecked == true ? IconFit.Fill : IconFit.Fit;

        private List<int> ChosenSizes() => SizeBoxes.Where(b => b.IsChecked == true).Select(b => int.Parse((string)b.Tag)).ToList();

        private void SaveSettings() => JsonSettings.Save(IconMakerManager.SettingsName, new IconMakerSettings { Fit = Fit, Sizes = ChosenSizes() });

        private void UpdateHint() =>
            TxtFitHint.Text = Fit == IconFit.Fit
                ? "Keeps the whole picture and adds see-through space if it is not square."
                : "Fills the square and crops the edges of a picture that is not square.";

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _applying) return;
            UpdateHint();
            BuildPreview();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // =================================================================
        //  Picture in
        // =================================================================

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("Str_IconMaker_MsgChoosePicture", "Choose a picture"),
                Filter = Loc.T("Str_IconMaker_MsgPictureFilter", "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp|All files|*.*"),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) LoadPicture(dialog.FileName);
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
            string? file = files.FirstOrDefault(f => File.Exists(f) && Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (file == null) { ShowStatus(Loc.T("Str_IconMaker_MsgNotPicture", "That isn't a picture I can open."), error: true); return; }
            LoadPicture(file);
        }

        private async void LoadPicture(string path)
        {
            var result = await Task.Run(() =>
            {
                var bgra = SpritePackerManager.LoadBgra(path, out int w, out int h, out string problem);
                return (bgra, w, h, problem);
            });
            if (result.bgra == null)
            {
                ShowStatus(result.problem == "ImageTooBig" ? "That picture is too big to open." : "That picture couldn't be opened.", error: true);
                return;
            }
            _bgra = result.bgra;
            _width = result.w;
            _height = result.h;
            _name = Path.GetFileNameWithoutExtension(path);
            TxtFileName.Text = Path.GetFileName(path);
            TxtSourceInfo.Text = Loc.F("Str_IconMaker_MsgSourceInfo", "{0} × {1} px", _width, _height) + (Math.Min(_width, _height) < 256 ? " - " + Loc.T("Str_IconMaker_MsgBiggerHint", "a bigger picture gives sharper large icons.") : "");
            StatusBanner.Visibility = Visibility.Collapsed;
            HeroImage.Source = SpritePackerManager.CreateBitmap(_bgra, _width, _height);
            HeroPlaceholder.Visibility = Visibility.Collapsed;
            HeroName.Text = Path.GetFileName(path);
            HeroInfo.Text = TxtSourceInfo.Text;
            BuildPreview();
        }

        // =================================================================
        //  Preview, saving
        // =================================================================

        private void BuildPreview()
        {
            if (_bgra == null)
            {
                PreviewList.ItemsSource = null;
                PreviewEmpty.Visibility = Visibility.Visible;
                BtnIco.IsEnabled = BtnSet.IsEnabled = false;
                return;
            }

            var sizes = ChosenSizes();
            _pictures.Clear();
            foreach (int size in sizes)
            {
                var square = IconMakerManager.ToSquare(_bgra, _width, _height, size, Fit);
                _pictures.Add((size, SpritePackerManager.CreateBitmap(square, size, size)));
            }
            ShowCards();
            PreviewEmpty.Visibility = sizes.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            BtnIco.IsEnabled = sizes.Count > 0;
            BtnSet.IsEnabled = true;
            if (sizes.Count == 0) ShowStatus(Loc.T("Str_IconMaker_MsgTickSize", "Tick at least one size for the .ico."), error: false);
            else StatusBanner.Visibility = Visibility.Collapsed;
        }

        // =================================================================
        //  Zoom (mouse wheel) and the per-size cards
        // =================================================================

        /// <summary>One card per size, drawn at size x zoom.</summary>
        private void ShowCards()
        {
            var items = _pictures
                .Select(p =>
                {
                    int display = Math.Max(1, (int)Math.Round(p.Size * _zoom));
                    return new IconPreview(p.Size, display, display + 8, $"{p.Size} × {p.Size}", UseNote(p.Size), p.Picture);
                })
                .ToList();
            PreviewList.ItemsSource = items;
            ZoomBadge.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtZoom.Text = "×" + _zoom.ToString("0.##", CultureInfo.InvariantCulture);
            FitWrapWidth(items);
        }

        /// <summary>
        /// The cards wrap to the width of the window; if one card is wider than that (a big zoom), the area gets
        /// exactly that wide and the horizontal scroll bar appears instead of the card being cut off.
        /// </summary>
        private void FitWrapWidth(IReadOnlyList<IconPreview>? items = null)
        {
            items ??= PreviewList.ItemsSource as IReadOnlyList<IconPreview>;
            if (items == null || items.Count == 0) { PreviewList.ClearValue(WidthProperty); return; }
            double widest = items.Max(i => i.Box) + 70;   // card padding and margins around the picture
            double room = Math.Max(0, PreviewScroll.ActualWidth - 12);
            PreviewList.Width = Math.Max(room, widest);
        }

        private void PreviewScroll_SizeChanged(object sender, SizeChangedEventArgs e) => FitWrapWidth();

        private static string UseNote(int size) => size switch
        {
            16 => "Title bars, small lists",
            24 => "Toolbars",
            32 => "Desktop shortcuts, lists",
            48 => "Large lists, Explorer",
            64 => "Large views",
            128 => "Big thumbnails",
            256 => "Largest, the jumbo size",
            _ => "",
        };

        private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_bgra == null || _pictures.Count == 0) return;
            double next = e.Delta > 0 ? _zoom * 1.25 : _zoom / 1.25;
            _zoom = Math.Clamp(Math.Round(next, 3), 0.5, 8);
            ShowCards();
            e.Handled = true;   // the wheel zooms here; the scroll bars still scroll
        }

        private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => ResetZoom();

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) ResetZoom();
        }

        private void ResetZoom()
        {
            _zoom = 1;
            ShowCards();
        }

        private void BtnSavePng_Click(object sender, RoutedEventArgs e)
        {
            if (_bgra == null || (sender as FrameworkElement)?.Tag is not int size) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("Str_IconMaker_MsgSavePngTitle", "Save PNG"), Filter = Loc.T("Str_IconMaker_MsgPngFilter", "PNG picture|*.png"), FileName = $"{(_name.Length > 0 ? _name : "icon")}-{size}.png", OverwritePrompt = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                AtomicFile.WriteAllBytes(dialog.FileName, IconMakerManager.ToPng(IconMakerManager.ToSquare(_bgra, _width, _height, size, Fit), size));
                ShowDone(Loc.F("Str_IconMaker_MsgSaved", "Saved {0}", Path.GetFileName(dialog.FileName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus(Loc.F("Str_IconMaker_MsgCouldntSave", "Couldn't save: {0}", ex.Message), error: true);
            }
        }

        private void BtnIco_Click(object sender, RoutedEventArgs e)
        {
            if (_bgra == null) return;
            var sizes = ChosenSizes();
            if (sizes.Count == 0) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("Str_IconMaker_MsgSaveIconTitle", "Save icon"), Filter = Loc.T("Str_IconMaker_MsgIcoFilter", "Icon|*.ico"), FileName = (_name.Length > 0 ? _name : "icon") + ".ico", OverwritePrompt = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                var ico = IconMakerManager.BuildIcoFrom(_bgra, _width, _height, sizes, Fit);
                AtomicFile.WriteAllBytes(dialog.FileName, ico);
                ShowDone(Loc.F("Str_IconMaker_MsgSavedSizes", "Saved {0} · {1} sizes", Path.GetFileName(dialog.FileName), sizes.Count));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus(Loc.F("Str_IconMaker_MsgCouldntSave", "Couldn't save: {0}", ex.Message), error: true);
            }
        }

        private void BtnSet_Click(object sender, RoutedEventArgs e)
        {
            if (_bgra == null) return;
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Str_IconMaker_MsgFaviconTitle", "Choose where to put the favicon folder") };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                string folder = SpritePackerManager.UniqueFolder(Path.Combine(dialog.FolderName, "favicons"));
                Directory.CreateDirectory(folder);
                var files = IconMakerManager.BuildFaviconSet(_bgra, _width, _height, Fit, _name);
                foreach (var (name, data) in files) AtomicFile.WriteAllBytes(Path.Combine(folder, name), data);
                ShowDone(Loc.F("Str_IconMaker_MsgSavedFiles", "Saved {0} files in {1}", files.Count, Path.GetFileName(folder)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus(Loc.F("Str_IconMaker_MsgCouldntSave", "Couldn't save: {0}", ex.Message), error: true);
            }
        }

        // =================================================================
        //  Messages
        // =================================================================

        private void ShowDone(string text)
        {
            TxtDonePill.Text = text;
            DonePill.Visibility = Visibility.Visible;
            _doneTimer.Stop();
            _doneTimer.Start();
        }

        private void ShowStatus(string text, bool error)
        {
            StatusBanner.SetResourceReference(Border.BackgroundProperty, error ? "StatusErrorBg" : "StatusWarningBg");
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "StatusErrorFg" : "StatusWarningFg");
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
        }

        public void CancelActiveRun()
        {
            _doneTimer.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
