using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Clipboard = System.Windows.Clipboard;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;
using Rectangle = System.Windows.Shapes.Rectangle;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// OCR Text Extractor (Screen to Text) - UI only. Capture and reading live
    /// in <see cref="ScreenOcrManager"/>; the drag-to-select overlay is
    /// <see cref="ScreenOcrSelectionWindow"/>. Sources: a screen area, an image
    /// file (open or drop) or a picture on the clipboard.
    /// </summary>
    public partial class ScreenOcrView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DialogTitle = "OCR Text Extractor";
        private const int MaxDecodePixels = 6000;   // bigger photos are decoded smaller - OCR shrinks them anyway

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".jfif", ".ico" };

        private readonly bool _viewConstructed;
        private readonly DispatcherTimer _copyResetTimer;
        private ScreenOcrSettings _settings = new();
        private ScreenOcrImage? _source;
        private string _sourceLabel = "";
        private ScreenOcrResult? _result;
        private CancellationTokenSource? _cts;
        private int _runVersion;
        private bool _loadedOnce;
        private bool _applying;
        private bool _applyingText;
        private bool _textEdited;
        private bool _capturing;
        private bool _optionsCollapsed;
        private bool _closed;

        private enum StatusKind { Ready, Busy, Done, Empty, Error }

        /// <summary>A Str_Ocr_* string in the active language (the English text if the key is missing).</summary>
        private string S(string name, string fallback) => TryFindResource("Str_Ocr_" + name) as string ?? fallback;

        public ScreenOcrView()
        {
            InitializeComponent();
            _copyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _copyResetTimer.Tick += (_, _) =>
            {
                _copyResetTimer.Stop();
                IconCopy.Kind = PackIconKind.ContentCopy;
                BtnCopy.ToolTip = S("CopyTip", "Copy the text (or just the part you've selected)");
            };
            _viewConstructed = true;

            // Ctrl+Shift+X / Ctrl+O by default; the keys can be changed in Settings.
            ShortcutManager.Attach("ocr.capture", async () => await CaptureAsync());
            ShortcutManager.Attach("ocr.open", () => BtnOpenImage_Click(this, new RoutedEventArgs()));
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a read in progress.</summary>
        public void CancelActiveRun()
        {
            _closed = true;
            _cts?.Cancel();
            _copyResetTimer.Stop();
        }

        private bool Ignore => !_viewConstructed || _applying;

        // =================================================================
        //  Lifetime and settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            // Every time the page is shown: the engine and its languages may have been changed in Settings.
            _loadedOnce = true;
            _applying = true;   // clearing the language list fires SelectionChanged, which must not save
            try
            {
                _settings = ScreenOcrManager.LoadSettings();
                FillLanguages();
            }
            finally
            {
                _applying = false;
            }
            ApplySettings();
            Focus();   // so Ctrl+V and Ctrl+Shift+X work straight away
        }

        private void ApplySettings()
        {
            _applying = true;
            try
            {
                ChkHideWhileCapturing.IsChecked = _settings.HideWhileCapturing;
                ChkCopyWhenDone.IsChecked = _settings.CopyWhenDone;
                ChkEnhance.IsChecked = _settings.Enhance;
                RadioKeepLines.IsChecked = _settings.Layout == ScreenOcrLayout.KeepLines;
                RadioJoinLines.IsChecked = _settings.Layout == ScreenOcrLayout.JoinLines;
                SelectLanguage(UsingTesseract ? _settings.TesseractLanguage : _settings.Language);
            }
            finally
            {
                _applying = false;
            }
        }

        private void SaveSettings()
        {
            if (Ignore || _closed) return;
            ScreenOcrManager.SaveSettings(_settings);
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            _settings.HideWhileCapturing = ChkHideWhileCapturing.IsChecked == true;
            _settings.CopyWhenDone = ChkCopyWhenDone.IsChecked == true;
            SaveSettings();
        }

        private async void ChkEnhance_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            _settings.Enhance = ChkEnhance.IsChecked == true;
            SaveSettings();
            if (_source != null && !_capturing) await ReadAsync(_source, _sourceLabel);   // read the same picture again
        }

        private void BtnAddLanguage_Click(object sender, RoutedEventArgs e)
        {
            if (UsingTesseract)
            {
                SettingsView.RequestedSection = "TextRecognition";
                ShortcutManager.OpenToolRequested?.Invoke(ToolCatalogManager.SettingsId);
                return;
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }

        /// <summary>Click, not Checked: Checked also fires while settings are applied.</summary>
        private void Layout_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore || sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out ScreenOcrLayout layout)) return;
            _settings.Layout = layout;
            SaveSettings();
            if (_result == null || _result.IsEmpty) return;
            if (_textEdited)
            {
                SetStatus(StatusKind.Done, TxtStatus.Text, S("EditedKept", "You've edited the text, so it was left as it is. The new line breaks apply to the next capture."));
                return;
            }
            ShowText(_result);
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
            CollapseHeaderStack.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        // =================================================================
        //  Languages
        // =================================================================

        private bool UsingTesseract => _settings.Engine == ScreenOcrEngineKind.Tesseract;

        private void FillLanguages()
        {
            BtnAddLanguage.Content = UsingTesseract ? S("ManageLanguages", "Manage languages") : S("AddLanguage", "Add a language");

            if (UsingTesseract)
            {
                FillTesseractLanguages();
                return;
            }

            var installed = ScreenOcrManager.AvailableLanguages();
            var profile = ScreenOcrManager.ProfileLanguage();

            CmbLanguage.Items.Clear();
            if (installed.Count > 1)
                CmbLanguage.Items.Add(new ComboBoxItem { Tag = ScreenOcrManager.AutoLanguageTag, Content = S("LangAuto", "Auto-detect") });
            CmbLanguage.Items.Add(new ComboBoxItem
            {
                Tag = "",
                Content = profile != null
                    ? string.Format(S("SameAsWindowsNamed", "Same as Windows ({0})"), profile.DisplayName)
                    : S("SameAsWindows", "Same as Windows"),
            });
            foreach (var language in installed)
                CmbLanguage.Items.Add(new ComboBoxItem { Tag = language.Tag, Content = language.DisplayName });

            if (installed.Count == 0)
            {
                CmbLanguage.IsEnabled = false;
                TxtLanguageHint.Text = S("NoLanguage", "No OCR language is installed in Windows. Add one in Settings › Time & language › Language & region (the language's \"Optical character recognition\" part).");
                TxtLanguageHint.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarningFg");
            }
            else
            {
                TxtLanguageHint.Text = installed.Count == 1
                    ? S("OneLanguage", "To read another language, add it in Windows Settings › Time & language › Language & region.")
                    : string.Format(S("ManyLanguages", "{0} languages installed in Windows. Auto-detect tries all of them."), installed.Count);
                TxtLanguageHint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextMuted");
            }
        }

        /// <summary>Tesseract's list: what was installed in Settings, plus Auto-detect (all of them at once) when there are several.</summary>
        private void FillTesseractLanguages()
        {
            CmbLanguage.Items.Clear();
            if (!ScreenOcrManager.IsTesseractAvailable)
            {
                CmbLanguage.IsEnabled = false;
                TxtLanguageHint.Text = S("TesseractMissing", "Tesseract isn't in this copy of Smart Tools. Switch back to Windows OCR in Settings › Text recognition.");
                TxtLanguageHint.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarningFg");
                return;
            }

            var installed = ScreenOcrManager.TesseractLanguages();
            CmbLanguage.IsEnabled = true;
            if (installed.Count > 1)
                CmbLanguage.Items.Add(new ComboBoxItem { Tag = ScreenOcrManager.AutoLanguageTag, Content = S("LangAuto", "Auto-detect") });
            foreach (var language in installed)
                CmbLanguage.Items.Add(new ComboBoxItem { Tag = language.Tag, Content = language.DisplayName });

            TxtLanguageHint.Text = installed.Count > 1
                ? string.Format(S("TesseractMany", "{0} languages installed. Auto-detect tries all of them at once."), installed.Count)
                : S("TesseractOne", "Only English is installed. Add more languages in Settings › Text recognition.");
            TxtLanguageHint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextMuted");
        }

        private void SelectLanguage(string tag)
        {
            var match = CmbLanguage.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, tag, StringComparison.OrdinalIgnoreCase));
            // uninstalled since (or Auto-detect with fewer than two languages left) -> Same as Windows
            CmbLanguage.SelectedItem = match
                ?? CmbLanguage.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (UsingTesseract ? "eng" : ""))
                ?? CmbLanguage.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }

        private string SelectedLanguageTag => (CmbLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        private async void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Ignore) return;
            if (UsingTesseract) _settings.TesseractLanguage = SelectedLanguageTag;
            else _settings.Language = SelectedLanguageTag;
            SaveSettings();
            if (_source != null && !_capturing) await ReadAsync(_source, _sourceLabel);   // read the same picture again in the new language
        }

        // =================================================================
        //  Sources: screen, file, clipboard, drop
        // =================================================================

        private async void BtnCapture_Click(object sender, RoutedEventArgs e) => await CaptureAsync();

        private async Task CaptureAsync()
        {
            if (_capturing) return;
            _capturing = true;
            var main = Window.GetWindow(this);
            bool hide = ChkHideWhileCapturing.IsChecked == true && main != null;
            ScreenOcrImage? screen = null;
            Int32Rect? selection = null;
            try
            {
                if (hide)
                {
                    main!.Hide();
                    await Task.Delay(250);   // let Windows repaint what was behind the app
                }
                screen = await Task.Run(ScreenOcrManager.CaptureVirtualScreen);

                var overlay = new ScreenOcrSelectionWindow(ScreenOcrSelectionWindow.ToBitmapSource(screen), VirtualScreenInDips());
                overlay.ShowDialog();
                selection = overlay.Selection;
            }
            catch (Exception ex)
            {
                // Typically the secure desktop (UAC prompt, lock screen) - nothing can be captured then.
                SetStatus(StatusKind.Error, S("CaptureFailed", "Couldn't capture the screen"), ex.Message);
            }
            finally
            {
                if (hide && main != null)
                {
                    main.Show();
                    main.Activate();
                }
                _capturing = false;
                Focus();
            }

            if (_closed || screen == null || selection is not Int32Rect r) return;
            var crop = ScreenOcrManager.Crop(screen, r.X, r.Y, r.Width, r.Height);
            if (crop != null) await ReadAsync(crop, string.Format(S("ScreenAreaLabel", "Screen area {0} × {1}"), crop.Width, crop.Height));
        }

        /// <summary>The whole virtual screen (all monitors) in WPF units, for the overlay window.</summary>
        private Rect VirtualScreenInDips()
        {
            var px = System.Windows.Forms.SystemInformation.VirtualScreen;
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Rect(px.Left / dpi.DpiScaleX, px.Top / dpi.DpiScaleY, px.Width / dpi.DpiScaleX, px.Height / dpi.DpiScaleY);
        }

        private async void BtnOpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = S("OpenTitle", "Open a picture to read"),
                Filter = S("FilterPictures", "Pictures") + "|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.ico|"
                         + S("FilterAll", "All files") + "|*.*",
            };
            if (dialog.ShowDialog() == true) await ReadFileAsync(dialog.FileName);
        }

        private async void BtnPasteImage_Click(object sender, RoutedEventArgs e) => await PasteAsync();

        private async Task PasteAsync()
        {
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource picture)
                {
                    await ReadAsync(ToOcrImage(picture), S("PastedPicture", "Pasted picture"));
                    return;
                }
                if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsImageFile) is string file)
                {
                    await ReadFileAsync(file);
                    return;
                }
                SetStatus(StatusKind.Ready, S("NoClipTitle", "No picture on the clipboard"),
                    S("NoClipHint", "Copy a picture first - for example with Win+Shift+S - then paste it here."));
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
            {
                SetStatus(StatusKind.Error, S("ClipFailTitle", "Couldn't read the clipboard"),
                    S("ClipBusy", "Another program has it open. Try again in a moment."));
            }
        }

        private async Task ReadFileAsync(string path)
        {
            ScreenOcrImage image;
            try
            {
                image = ToOcrImage(LoadPicture(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or COMException)
            {
                SetStatus(StatusKind.Error, string.Format(S("OpenFailTitle", "Couldn't open {0}"), System.IO.Path.GetFileName(path)), ex is NotSupportedException or FileFormatException
                    ? S("CodecHint", "Windows can't decode this kind of picture (install its codec from the Microsoft Store, or save it as PNG or JPG).")
                    : ex.Message);
                return;
            }
            await ReadAsync(image, System.IO.Path.GetFileName(path));
        }

        /// <summary>Through a FileStream (a # or % in the name breaks Uri loading), decoded no bigger than needed.</summary>
        private static BitmapSource LoadPicture(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            int width = header.PixelWidth, height = header.PixelHeight;
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            if (Math.Max(width, height) > MaxDecodePixels)
            {
                if (width >= height) image.DecodePixelWidth = MaxDecodePixels;
                else image.DecodePixelHeight = MaxDecodePixels;
            }
            image.EndInit();
            image.Freeze();
            return image;
        }

        private static ScreenOcrImage ToOcrImage(BitmapSource source)
        {
            var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);
            var image = new ScreenOcrImage(pixels, w, h);
            ScreenOcrManager.FlattenOnWhite(image);
            return image;
        }

        private static bool IsImageFile(string path) =>
            ImageExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        private static string? DroppedImage(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
                ? files.FirstOrDefault(IsImageFile)
                : null;

        private void Root_PreviewDragOver(object sender, DragEventArgs e)
        {
            bool ok = DroppedImage(e) != null;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok && DropHighlight.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropHighlight, true);
            e.Handled = ok;
        }

        private void Root_PreviewDragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
                UiMotion.FadeVisibility(DropHighlight, false);
        }

        private async void Root_PreviewDrop(object sender, DragEventArgs e)
        {
            UiMotion.FadeVisibility(DropHighlight, false);
            if (DroppedImage(e) is not string file) return;
            e.Handled = true;
            await ReadFileAsync(file);
        }

        private async void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Capture and Open are shortcut actions (see the constructor); Ctrl+V stays here.
            var mods = Keyboard.Modifiers;
            if (e.Key == Key.V && mods == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
            {
                // In the text box, Ctrl+V pastes text as usual.
                e.Handled = true;
                await PasteAsync();
            }
        }

        // =================================================================
        //  Reading
        // =================================================================

        private async Task ReadAsync(ScreenOcrImage image, string label)
        {
            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            int version = ++_runVersion;

            _source = image;
            _sourceLabel = label;
            _result = null;
            ShowPreview(image);
            SetStatus(StatusKind.Busy, S("Reading", "Reading text…"), label);

            try
            {
                var result = await ScreenOcrManager.RecognizeAsync(image, SelectedLanguageTag, cts.Token, _settings.Enhance, _settings.Engine);
                if (version != _runVersion || _closed) return;
                _result = result;
                ShowResult(result, label);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer read, or the app is closing
            }
            catch (InvalidOperationException ex)
            {
                if (version == _runVersion) SetStatus(StatusKind.Error, S("ReadFailed", "Couldn't read the text"), ex.Message);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or OutOfMemoryException or System.ComponentModel.Win32Exception)
            {
                if (version == _runVersion) SetStatus(StatusKind.Error, S("ReadFailed", "Couldn't read the text"), ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_cts, cts)) _cts = null;
                cts.Dispose();
            }
        }

        private void ShowResult(ScreenOcrResult result, string label)
        {
            ShowText(result);
            DrawWords(result);

            string seconds = result.Elapsed.TotalSeconds < 0.1 ? "<0.1 s" : $"{result.Elapsed.TotalSeconds:0.0} s";
            LanguagePill.Visibility = Visibility.Visible;
            TxtLanguagePill.Text = $"{(UsingTesseract ? S("EngineTesseract", "Tesseract") : S("EngineWindows", "Windows OCR"))} · {result.LanguageName}";

            if (result.IsEmpty)
            {
                WordsPill.Visibility = Visibility.Collapsed;
                SetStatus(StatusKind.Empty, S("NoTextTitle", "No text found"),
                    string.Format(S("NoTextHint", "{0} · Try a bigger area around the text, or pick the language it's written in."), label));
                return;
            }

            WordsPill.Visibility = Visibility.Visible;
            TxtWordsPill.Text = $"{ScreenOcrManager.Plural(result.WordCount, S("Word", "word"), S("Words", "words"))} · {ScreenOcrManager.Plural(result.LineCount, S("Line", "line"), S("Lines", "lines"))}";
            UiMotion.Pop(WordsPill, 0.9);

            bool copied = ChkCopyWhenDone.IsChecked == true && CopyToClipboard(TxtResult.Text);
            SetStatus(StatusKind.Done,
                copied ? S("TextCopied", "Text copied - ready to paste") : S("TextExtracted", "Text extracted"),
                $"{label} · {seconds}" + (copied ? " · " + S("OnClipboard", "on the clipboard") : ""));
        }

        private void ShowText(ScreenOcrResult result)
        {
            _applyingText = true;
            try
            {
                TxtResult.Text = ScreenOcrManager.FormatText(result.Lines, _settings.Layout);
                TxtResult.ScrollToHome();
                _textEdited = false;
            }
            finally
            {
                _applyingText = false;
            }
            UiMotion.FadeIn(TxtResult, 4);
            UpdateButtons();
        }

        private void ShowPreview(ScreenOcrImage image)
        {
            PreviewImage.Source = ScreenOcrSelectionWindow.ToBitmapSource(image);
            PreviewHost.Width = image.Width;
            PreviewHost.Height = image.Height;
            WordLayer.Children.Clear();
            if (EmptyPanel.Visibility == Visibility.Visible)
            {
                EmptyPanel.Visibility = Visibility.Collapsed;
                PreviewBox.Visibility = Visibility.Visible;
            }
            UiMotion.FadeIn(PreviewBox, 4);
            UpdateButtons();
        }

        /// <summary>A soft accent box on every word that was read; hover shows what it was read as.</summary>
        private void DrawWords(ScreenOcrResult result)
        {
            WordLayer.Children.Clear();
            foreach (var word in result.Words)
            {
                var box = new Rectangle
                {
                    Width = Math.Max(1, word.Width + 2),
                    Height = Math.Max(1, word.Height + 2),
                    RadiusX = 2,
                    RadiusY = 2,
                    Opacity = 0.28,
                    ToolTip = word.Text,
                };
                box.SetResourceReference(Shape.FillProperty, "AccentColor");
                box.MouseEnter += (_, _) => box.Opacity = 0.5;
                box.MouseLeave += (_, _) => box.Opacity = 0.28;
                Canvas.SetLeft(box, word.X - 1);
                Canvas.SetTop(box, word.Y - 1);
                WordLayer.Children.Add(box);
            }
            WordLayer.Visibility = ChkShowWords.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChkShowWords_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            WordLayer.Visibility = ChkShowWords.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // =================================================================
        //  Status
        // =================================================================

        private void SetStatus(StatusKind kind, string title, string detail)
        {
            TxtStatus.Text = title;
            TxtStatusDetail.Text = detail;
            TxtStatusDetail.ToolTip = detail;
            ReadingBar.Visibility = kind == StatusKind.Busy ? Visibility.Visible : Visibility.Collapsed;

            (PackIconKind icon, string bg, string fg) = kind switch
            {
                StatusKind.Busy => (PackIconKind.TextBoxSearch, "AccentColorSubtle", "AccentColor"),
                StatusKind.Done => (PackIconKind.CheckCircleOutline, "StatusSuccessBg", "StatusSuccessFg"),
                StatusKind.Empty => (PackIconKind.TextBoxRemoveOutline, "StatusWarningBg", "StatusWarningFg"),
                StatusKind.Error => (PackIconKind.AlertCircleOutline, "StatusErrorBg", "StatusErrorFg"),
                _ => (PackIconKind.TextRecognition, "AccentColorSubtle", "AccentColor"),
            };
            IconStatus.Kind = icon;
            StatusChip.SetResourceReference(Border.BackgroundProperty, bg);
            IconStatus.SetResourceReference(ForegroundProperty, fg);
            if (kind != StatusKind.Busy) UiMotion.Pop(StatusChip, 0.85);
        }

        // =================================================================
        //  Text actions
        // =================================================================

        private void TxtResult_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (!_applyingText) _textEdited = true;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool hasText = !string.IsNullOrWhiteSpace(TxtResult.Text);
            BtnCopy.IsEnabled = hasText;
            BtnSave.IsEnabled = hasText;
            BtnClear.IsEnabled = hasText || _source != null;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            string text = TxtResult.SelectionLength > 0 ? TxtResult.SelectedText : TxtResult.Text;
            if (!CopyToClipboard(text))
            {
                SetStatus(StatusKind.Error, S("CopyFailTitle", "Couldn't copy"),
                    S("CopyFailHint", "Another program has the clipboard open. Try again in a moment."));
                return;
            }
            IconCopy.Kind = PackIconKind.Check;
            BtnCopy.ToolTip = TxtResult.SelectionLength > 0 ? S("SelectionCopied", "Selection copied") : S("Copied", "Copied");
            UiMotion.Pop(BtnCopy, 0.92);
            _copyResetTimer.Stop();
            _copyResetTimer.Start();
        }

        private static bool CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                return false;   // the clipboard is busy - the caller says so
            }
        }

        /// <summary>A dated name; a taken name gets " (2)" - a save never replaces another file.</summary>
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = S("SaveTitle", "Save the extracted text"),
                Filter = S("FilterTxt", "Text file (*.txt)") + "|*.txt",
                FileName = ScreenOcrManager.SaveFileName(DateTime.Now),
                AddExtension = true,
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog() != true) return;

            string path = OutputFolderManager.UniquePath(dialog.FileName);
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true)))
                    writer.Write(TxtResult.Text);
                string note = path != dialog.FileName ? " (" + S("NameTaken", "that name was taken, so it got its own") + ")" : "";
                SetStatus(StatusKind.Done, S("Saved", "Text saved"), path + note);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus(StatusKind.Error, S("SaveFailed", "Couldn't save the text"), ex.Message);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _runVersion++;
            _source = null;
            _result = null;
            _applyingText = true;
            try { TxtResult.Clear(); }
            finally { _applyingText = false; }
            _textEdited = false;

            PreviewImage.Source = null;
            WordLayer.Children.Clear();
            PreviewBox.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            UiMotion.FadeIn(EmptyPanel, 4);
            WordsPill.Visibility = LanguagePill.Visibility = Visibility.Collapsed;
            SetStatus(StatusKind.Ready, S("ReadyTitle", "Ready to read text from your screen"),
                S("ReadyHint", "Select screen area, open an image, or paste one (Ctrl+V)."));
            UpdateButtons();
        }
    }
}
