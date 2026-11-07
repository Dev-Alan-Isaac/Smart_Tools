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
        /// <summary>One size in the real-size column. <c>Box</c> is the picture's frame (the size plus a 4 px margin each side).</summary>
        public sealed record IconPreview(int Size, int Box, string Label, string Note, BitmapSource Picture, bool IsSelected);

        private readonly List<(int Size, BitmapSource Picture)> _pictures = new();
        private int _selectedSize;

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
                ? Loc.T("Str_IconMaker_MsgFitHint", "Keeps the whole picture and adds see-through space if it is not square.")
                : Loc.T("Str_IconMaker_MsgFillHint", "Fills the square and crops the edges of a picture that is not square.");

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
                ShowStatus(result.problem == "ImageTooBig" ? Loc.T("Str_IconMaker_MsgTooBig", "That picture is too big to open.") : Loc.T("Str_IconMaker_MsgCantOpen", "That picture couldn't be opened."), error: true);
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
                PreviewArea.Visibility = Visibility.Collapsed;
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
            if (!sizes.Contains(_selectedSize)) _selectedSize = sizes.Count > 0 ? sizes.Max() : 0;
            ShowCards();
            PreviewArea.Visibility = sizes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PreviewEmpty.Visibility = sizes.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            BtnIco.IsEnabled = sizes.Count > 0;
            BtnSet.IsEnabled = true;
            if (sizes.Count == 0) ShowStatus(Loc.T("Str_IconMaker_MsgTickSize", "Tick at least one size for the .ico."), error: false);
            else StatusBanner.Visibility = Visibility.Collapsed;
        }

        // =================================================================
        //  Stage (the chosen size, fitted to the free space) and the real-size column
        // =================================================================

        /// <summary>The column lists every size at its real pixel size; the stage shows the chosen one as large as the space allows.</summary>
        private void ShowCards()
        {
            var items = _pictures
                .Select(p => new IconPreview(p.Size, p.Size + 8, $"{p.Size} × {p.Size}", UseNote(p.Size), p.Picture, p.Size == _selectedSize))
                .OrderByDescending(i => i.Size)
                .ToList();
            PreviewList.ItemsSource = items;
            ShowStage();
        }

        private void ShowStage()
        {
            var chosen = _pictures.FirstOrDefault(p => p.Size == _selectedSize);
            if (chosen.Picture == null) { StageImage.Source = null; StageTitle.Text = StageNote.Text = ""; return; }
            StageImage.Source = chosen.Picture;
            StageTitle.Text = $"{chosen.Size} × {chosen.Size}";
            StageNote.Text = UseNote(chosen.Size);
        }

        private static string UseNote(int size) => size switch
        {
            16 => Loc.T("Str_IconMaker_Use16", "Title bars, small lists"),
            24 => Loc.T("Str_IconMaker_Use24", "Toolbars"),
            32 => Loc.T("Str_IconMaker_Use32", "Desktop shortcuts, lists"),
            48 => Loc.T("Str_IconMaker_Use48", "Large lists, Explorer"),
            64 => Loc.T("Str_IconMaker_Use64", "Large views"),
            128 => Loc.T("Str_IconMaker_Use128", "Big thumbnails"),
            256 => Loc.T("Str_IconMaker_Use256", "Largest, the jumbo size"),
            _ => "",
        };

        private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not int size || size == _selectedSize) return;
            _selectedSize = size;
            ShowCards();
        }

        private void BtnSaveStage_Click(object sender, RoutedEventArgs e) => SavePng(_selectedSize);

        private void BtnSavePng_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is int size) SavePng(size);
            e.Handled = true;
        }

        private void SavePng(int size)
        {
            if (_bgra == null || size <= 0) return;
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
