using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The Translation languages section of Settings - UI only. The pair list, downloads, and the check for a new
    /// pair are <see cref="TranslateManager"/>; the manager is shared with the Translate page.
    /// </summary>
    public partial class TranslateSettingsPanel : UserControl
    {
        /// <summary>A setting was saved; the host shows its "Saved" pill.</summary>
        public event Action? SettingChanged;

        private readonly ObservableCollection<TranslateModelItem> _items = new();
        private readonly Dictionary<string, CancellationTokenSource> _running = new();

        public TranslateSettingsPanel()
        {
            InitializeComponent();
            ListModels.ItemsSource = _items;
            TranslateManager.PairsChanged += Load;   // also raised by this panel when a pair is added or taken off
        }

        private string S(string name, string fallback) => TryFindResource("Str_Settings_Translate" + name) as string ?? fallback;

        private string LanguageName(string code)
        {
            if (TryFindResource($"Str_Translate_Lang_{code}") is string name) return name;
            try { return System.Globalization.CultureInfo.GetCultureInfo(code).DisplayName; }
            catch (System.Globalization.CultureNotFoundException) { return code.ToUpperInvariant(); }
        }

        /// <summary>The languages shown as clickable short codes. Japanese is "ja"; the add check also tries "jap" when a model uses that.</summary>
        private static readonly string[] KnownCodes =
        {
            "en", "es", "fr", "de", "it", "pt", "nl", "ca", "ru", "zh", "ja", "ar",
            "af", "sq", "hy", "az", "eu", "be", "bn", "bg", "my", "hr", "cs", "da", "et", "fi", "gl", "ka", "el", "gu",
            "he", "hi", "hu", "is", "id", "ga", "kn", "kk", "km", "ko", "lo", "lv", "lt", "mk", "ms", "ml", "mt", "mr",
            "mn", "ne", "no", "fa", "pl", "pa", "ro", "sr", "si", "sk", "sl", "sw", "sv", "tl", "ta", "te", "th", "tr",
            "uk", "ur", "uz", "vi", "cy", "yo", "zu",
        };

        /// <summary>One grid row per language. Rebuilt on every Load so the names follow the app language.</summary>
        private sealed record LanguageRow(string Name, string Code);

        private void BuildCodeRows() =>
            GridCodes.ItemsSource = KnownCodes.Select(c => new LanguageRow(LanguageName(c), c)).ToList();

        /// <summary>Left click on a language fills From.</summary>
        private void GridCodes_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (RowUnder(e) is not { } row) return;
            GridCodes.SelectedItem = null;
            TxtAddFrom.Text = row.Code;
            TxtAddTo.Focus();
        }

        /// <summary>Right click on a language fills To.</summary>
        private void GridCodes_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (RowUnder(e) is not { } row) return;
            TxtAddTo.Text = row.Code;
            BtnAddPair.Focus();
            e.Handled = true;
        }

        /// <summary>The language under the pointer, or null for headers, the scroll bar, and empty space.</summary>
        private static LanguageRow? RowUnder(System.Windows.Input.MouseButtonEventArgs e)
        {
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node != null && node is not DataGridRow)
                node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            return (node as DataGridRow)?.Item as LanguageRow;
        }

        /// <summary>Shows the pairs and which are downloaded. Click handlers only fire for the user, so nothing needs guarding.</summary>
        public void Load()
        {
            BuildCodeRows();
            TxtModelsRoot.Text = TranslateManager.ModelsRoot;

            var existing = _items.ToDictionary(i => i.Pair.Id);
            _items.Clear();
            foreach (var pair in TranslateManager.Pairs)
            {
                _items.Add(existing.TryGetValue(pair.Id, out var item)
                    ? item
                    : new TranslateModelItem(pair, $"{LanguageName(pair.FromCode)} → {LanguageName(pair.ToCode)}",
                                             TranslateManager.FolderOf(pair), TranslateManager.IsCustom(pair)));
            }

            foreach (var item in _items.Where(i => !i.IsBusy))
            {
                item.IsInstalled = TranslateManager.IsInstalled(item.Pair);
                item.Detail = DetailFor(item);
            }
            UpdateCount();
        }

        private string DetailFor(TranslateModelItem item) => item.IsInstalled
            ? string.Format(S("DiskSize", "{0} MB on disk"), (TranslateManager.SizeOf(item.Pair) / 1048576.0).ToString("N0"))
            : string.Format(S("DownloadSize", "About {0} MB to download"), item.Pair.ApproxMb);

        private void UpdateCount() =>
            TxtModelsCount.Text = string.Format(S("InstalledCount", "{0} installed"), _items.Count(i => i.IsInstalled));

        private static TranslateModelItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as TranslateModelItem;

        // ---- Location -------------------------------------------------------------------------

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!TranslateManager.OpenInExplorer(TranslateManager.ModelsRoot))
                TxtLocationNote.Text = S("OpenFailed", "Couldn't open the folder.");
        }

        private void BtnCopyPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TranslateManager.ModelsRoot);
                TxtLocationNote.Text = S("PathCopied", "Path copied.");
            }
            catch (COMException)
            {
                TxtLocationNote.Text = S("CopyFailed", "Couldn't copy. Another app is using the clipboard.");   // another app holds the clipboard
            }
        }

        // ---- Models ---------------------------------------------------------------------------

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { IsBusy: false } item) return;

            var cts = new CancellationTokenSource();
            _running[item.Pair.Id] = cts;
            item.Progress = 0;
            item.IsBusy = true;
            item.Detail = string.Format(S("Downloading", "Downloading… {0}%"), 0);
            var progress = new Progress<double>(v =>
            {
                item.Progress = v * 100;
                item.Detail = string.Format(S("Downloading", "Downloading… {0}%"), (int)(v * 100));
            });

            string? error = null;
            try
            {
                error = await TranslateManager.Shared.InstallAsync(item.Pair, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelled: nothing was kept, and the row goes back to "not downloaded".
            }
            finally
            {
                _running.Remove(item.Pair.Id);
                cts.Dispose();
                item.IsBusy = false;
            }

            item.IsInstalled = TranslateManager.IsInstalled(item.Pair);
            item.Detail = error ?? DetailFor(item);
            UpdateCount();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item && _running.TryGetValue(item.Pair.Id, out var cts)) cts.Cancel();
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } item) return;
            if (!TranslateManager.Shared.Remove(item.Pair))
            {
                item.Detail = S("RemoveFailed", "Couldn't delete the model files. Close anything using them (a translation may be running) and try again.");
                return;
            }
            item.IsInstalled = TranslateManager.IsInstalled(item.Pair);
            item.Detail = DetailFor(item);
            UpdateCount();
        }

        /// <summary>Takes a pair the user added off the list (and deletes its model).</summary>
        private void BtnForget_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { IsCustom: true } item) return;
            if (!TranslateManager.Shared.RemoveCustomPair(item.Pair))
            {
                item.Detail = S("RemoveFailed", "Couldn't delete the model files. Close anything using them (a translation may be running) and try again.");
                return;
            }
            SettingChanged?.Invoke();   // the list reloads through PairsChanged
        }

        // ---- Add a pair -----------------------------------------------------------------------

        private async void BtnAddPair_Click(object sender, RoutedEventArgs e)
        {
            BtnAddPair.IsEnabled = false;
            TxtAddStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            TxtAddStatus.Text = S("Checking", "Checking Hugging Face…");

            var (pair, error) = await TranslateManager.AddCustomPairAsync(TxtAddFrom.Text, TxtAddTo.Text, CancellationToken.None);
            BtnAddPair.IsEnabled = true;

            if (pair == null)
            {
                TxtAddStatus.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                TxtAddStatus.Text = error ?? "";
                return;
            }

            TxtAddFrom.Clear();
            TxtAddTo.Clear();
            TxtAddStatus.Text = string.Format(S("Added", "Added {0} to {1}. Download it below, then pick it in Translate."), pair.FromCode, pair.ToCode);
            SettingChanged?.Invoke();
        }

        /// <summary>Leaving Settings stops unfinished downloads; the manager deletes their half-done files.</summary>
        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            foreach (var cts in _running.Values.ToList()) cts.Cancel();
        }
    }
}
