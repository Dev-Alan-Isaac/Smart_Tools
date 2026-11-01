using Smart_Tools.WPF.Controllers;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Image to Pixel Art - UI only. The shrinking, palettes and dithering are <see cref="ImageToPixelManager"/>'s.</summary>
    public partial class ImageToPixelView : UserControl
    {
        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp" };

        private readonly DispatcherTimer _runTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _doneTimer;
        private readonly bool _viewConstructed;
        private bool _loadedOnce;
        private bool _applying;
        private int _run;
        private byte[]? _bgra;
        private int _width, _height;
        private string _name = "";
        private PixelizeResult? _result;

        public ImageToPixelView()
        {
            InitializeComponent();
            CmbPalette.Items.Add(new ComboBoxItem { Content = "From the picture", Tag = "Auto" });
            foreach (var p in PixelArtManager.Palettes) CmbPalette.Items.Add(new ComboBoxItem { Content = p.Name + " (" + p.Colors.Length + " colors)", Tag = p.Name });
            foreach (int scale in ImageToPixelManager.Scales) CmbScale.Items.Add(new ComboBoxItem { Content = scale + " ×", Tag = scale });

            _runTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _runTimer.Tick += (_, _) => { _runTimer.Stop(); _ = ProcessAsync(); };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); DonePill.Visibility = Visibility.Collapsed; };
            _viewConstructed = true;
        }

        // =================================================================
        //  Settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            var s = ImageToPixelManager.Clamp(JsonSettings.Load<ImageToPixelSettings>(ImageToPixelManager.SettingsName));
            _applying = true;
            TxtGrid.Text = s.GridWidth.ToString(CultureInfo.InvariantCulture);
            TxtColors.Text = s.Colors.ToString(CultureInfo.InvariantCulture);
            ChkDither.IsChecked = s.Dither;
            foreach (ComboBoxItem item in CmbPalette.Items) if ((string)item.Tag == s.Palette) CmbPalette.SelectedItem = item;
            foreach (ComboBoxItem item in CmbScale.Items) if ((int)item.Tag == s.ExportScale) CmbScale.SelectedItem = item;
            _applying = false;
            UpdatePanels();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private ImageToPixelSettings ReadSettings()
        {
            int Num(string t, int fallback) => int.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : fallback;
            return ImageToPixelManager.Clamp(new ImageToPixelSettings
            {
                GridWidth = Num(TxtGrid.Text, 48),
                Colors = Num(TxtColors.Text, 16),
                Palette = (CmbPalette.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto",
                Dither = ChkDither.IsChecked == true,
                ExportScale = (CmbScale.SelectedItem as ComboBoxItem)?.Tag is int scale ? scale : 8,
            });
        }

        private void SaveSettings() => JsonSettings.Save(ImageToPixelManager.SettingsName, ReadSettings());

        private void UpdatePanels()
        {
            PanelColors.Visibility = ReadSettings().Palette == "Auto" ? Visibility.Visible : Visibility.Collapsed;
            UpdateInfo();
        }

        private void UpdateInfo()
        {
            var s = ReadSettings();
            if (_bgra != null)
            {
                int gw = Math.Min(s.GridWidth, _width);
                int gh = Math.Max(1, (int)Math.Round((double)_height * gw / _width));
                TxtGridInfo.Text = $"Result: {gw} × {gh} pixels";
                TxtSaveInfo.Text = $"The PNG will be {gw * s.ExportScale:N0} × {gh * s.ExportScale:N0} px.";
            }
            else
            {
                TxtGridInfo.Text = "";
                TxtSaveInfo.Text = "";
            }
        }

        private void Changed()
        {
            if (!_viewConstructed || _applying) return;
            UpdatePanels();
            _runTimer.Stop();
            _runTimer.Start();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Option_TextChanged(object sender, TextChangedEventArgs e) => Changed();
        private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e) => Changed();
        private void Option_Click(object sender, RoutedEventArgs e) => Changed();

        private void GridChip_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is string t) TxtGrid.Text = t;
        }

        // =================================================================
        //  Picture in
        // =================================================================

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a picture",
                Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp|All files|*.*",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) LoadPicture(dialog.FileName);
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
            string? file = files.FirstOrDefault(f => File.Exists(f) && Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (file == null) { ShowStatus("That isn't a picture I can open.", error: true); return; }
            LoadPicture(file);
        }

        private async void LoadPicture(string path)
        {
            var loaded = await Task.Run(() =>
            {
                var bgra = SpritePackerManager.LoadBgra(path, out int w, out int h, out string problem);
                return (bgra, w, h, problem);
            });
            if (loaded.bgra == null)
            {
                ShowStatus(loaded.problem == "ImageTooBig" ? "That picture is too big to open." : "That picture couldn't be opened.", error: true);
                return;
            }
            _bgra = loaded.bgra;
            _width = loaded.w;
            _height = loaded.h;
            _name = Path.GetFileNameWithoutExtension(path);
            TxtFileName.Text = Path.GetFileName(path);
            TxtSourceInfo.Text = $"{_width} × {_height} px";
            StatusBanner.Visibility = Visibility.Collapsed;
            UpdateInfo();
            await ProcessAsync();
        }

        // =================================================================
        //  Making the pixel art
        // =================================================================

        private async Task ProcessAsync()
        {
            if (_bgra == null) return;
            int run = ++_run;
            var source = _bgra;
            int w = _width, h = _height;
            var settings = ReadSettings();
            var result = await Task.Run(() => ImageToPixelManager.Pixelize(source, w, h, settings));
            if (run != _run) return;

            _result = result;
            var bgra = ImageToPixelManager.ToBgra(result);
            ResultImage.Source = SpritePackerManager.CreateBitmap(bgra, result.Width, result.Height);
            ResultGrid.Width = result.Width;
            ResultGrid.Height = result.Height;
            ResultBox.Visibility = Visibility.Visible;
            PreviewEmpty.Visibility = Visibility.Collapsed;
            PillSize.Visibility = PillColors.Visibility = Visibility.Visible;
            TxtPillSize.Text = $"{result.Width} × {result.Height} px";
            TxtPillColors.Text = $"{result.ColorsUsed} color{(result.ColorsUsed == 1 ? "" : "s")}";
            BtnSave.IsEnabled = true;
            UpdateInfo();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            int scale = ReadSettings().ExportScale;
            var png = ImageToPixelManager.ToPng(_result, scale, out string? problem);
            if (png == null) { ShowStatus(problem ?? "Couldn't make the PNG.", error: true); return; }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save pixel art",
                Filter = "PNG picture|*.png",
                FileName = (_name.Length > 0 ? _name : "pixel-art") + "-pixel.png",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            string? error = SafeFile.WriteAllBytes(dialog.FileName, png);
            if (error != null) { ShowStatus("Couldn't save: " + error, error: true); return; }

            StatusBanner.Visibility = Visibility.Collapsed;
            TxtDonePill.Text = "Saved " + Path.GetFileName(dialog.FileName);
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
            _run++;
            _runTimer.Stop();
            _doneTimer.Stop();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
