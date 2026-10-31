using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl =System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The Text recognition section of Settings - UI only. Which engine Screen OCR uses is saved in its own
    /// settings file; installing and removing languages is <see cref="TesseractLanguageManager"/>.
    /// </summary>
    public partial class TextRecognitionSettingsPanel : UserControl
    {
        /// <summary>A setting was saved; the host shows its "Saved" pill.</summary>
        public event Action? SettingChanged;

        private readonly ObservableCollection<TesseractLanguageItem> _items = new();
        private readonly Dictionary<string, CancellationTokenSource> _running = new();

        public TextRecognitionSettingsPanel()
        {
            InitializeComponent();
            ListLanguages.ItemsSource = _items;
        }

        private string S(string name, string fallback) => TryFindResource("Str_Settings_Ocr" + name) as string ?? fallback;

        /// <summary>Shows the current engine and installed languages. Click handlers only fire for the user, so nothing needs guarding.</summary>
        public void Load()
        {
            var settings = ScreenOcrManager.LoadSettings();
            RadioEngineWindows.IsChecked = settings.Engine == ScreenOcrEngineKind.Windows;
            RadioEngineTesseract.IsChecked = settings.Engine == ScreenOcrEngineKind.Tesseract;
            RefreshEngineNote();

            ScreenOcrManager.EnsureTessdata();
            var installed = TesseractLanguageManager.InstalledCodes();
            if (_items.Count == 0)
                foreach (var language in TesseractData.Catalog)
                    _items.Add(new TesseractLanguageItem(language.Code, language.Display));

            foreach (var item in _items.Where(i => !i.IsBusy))
            {
                item.IsInstalled = installed.Contains(item.Code, StringComparer.OrdinalIgnoreCase);
                item.Detail = DetailFor(item);
            }
            UpdateCount();
        }

        private string DetailFor(TesseractLanguageItem item)
        {
            if (!item.IsInstalled) return "";
            if (!item.CanRemove) return S("Included", "Included with Smart Tools");
            long bytes = TesseractLanguageManager.SizeOf(item.Code);
            return bytes > 0 ? $"{bytes / 1048576.0:0.0} MB" : "";
        }

        private void UpdateCount() =>
            TxtLanguagesCount.Text = string.Format(S("InstalledCount", "{0} installed"), _items.Count(i => i.IsInstalled));

        private void RefreshEngineNote()
        {
            bool tesseract = RadioEngineTesseract.IsChecked == true;
            TxtEngineNote.Text = tesseract && !ScreenOcrManager.IsTesseractAvailable
                ? S("TesseractMissing", "Tesseract isn't in this copy of Smart Tools, so Screen OCR will ask you to switch back to Windows OCR.")
                : tesseract
                    ? S("TesseractNote", "Reads the languages you install below. Nothing leaves your PC, and only the language downloads use the internet.")
                    : S("WindowsNote", "Reads the languages installed in Windows. Nothing leaves your PC.");
            TxtEngineNote.SetResourceReference(TextBlock.ForegroundProperty,
                tesseract && !ScreenOcrManager.IsTesseractAvailable ? "StatusWarningFg" : "TextSecondary");
        }

        private void Engine_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out ScreenOcrEngineKind engine)) return;
            var settings = ScreenOcrManager.LoadSettings();
            settings.Engine = engine;
            ScreenOcrManager.SaveSettings(settings);
            RefreshEngineNote();
            SettingChanged?.Invoke();
        }

        private static TesseractLanguageItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as TesseractLanguageItem;

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } item || item.IsBusy) return;

            using var cts = new CancellationTokenSource();
            _running[item.Code] = cts;
            item.IsBusy = true;
            item.Progress = 0;
            item.Detail = S("Downloading", "Downloading...");
            var progress = new Progress<double>(p =>
            {
                item.Progress = p * 100;
                item.Detail = string.Format(S("DownloadingPercent", "Downloading... {0}%"), (int)(p * 100));
            });

            string? problem = null;
            try
            {
                problem = await TesseractLanguageManager.InstallAsync(item.Code, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                problem = S("Canceled", "Canceled");
            }
            finally
            {
                _running.Remove(item.Code);
                item.IsBusy = false;
            }

            item.IsInstalled = problem == null;
            item.Detail = problem ?? DetailFor(item);
            UpdateCount();
            if (problem == null) SettingChanged?.Invoke();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item && _running.TryGetValue(item.Code, out var cts)) cts.Cancel();
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } item) return;
            if (!TesseractLanguageManager.Remove(item.Code))
            {
                item.Detail = S("RemoveFailed", "Couldn't remove this language.");
                return;
            }

            item.IsInstalled = false;
            item.Detail = "";
            UpdateCount();

            // A removed language can't stay selected in Screen OCR.
            var settings = ScreenOcrManager.LoadSettings();
            if (settings.TesseractLanguage.Equals(item.Code, StringComparison.OrdinalIgnoreCase))
            {
                settings.TesseractLanguage = "eng";
                ScreenOcrManager.SaveSettings(settings);
            }
            SettingChanged?.Invoke();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            foreach (var cts in _running.Values.ToList()) cts.Cancel();
        }
    }
}
