using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Texture Pack Builder (Minecraft Java Edition) - UI only. Finding the pictures, matching them to Minecraft
    /// textures, checking them, pack.mcmeta, saving and the settings are in <see cref="TexturePackManager"/>.
    /// The pictures and their paths are never saved, only the setup.
    /// </summary>
    public partial class TexturePackView : UserControl
    {
        private readonly ObservableCollection<TextureSource> _sources = new();
        private readonly ObservableCollection<PackTexture> _textures = new();
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _validateTimer;
        private readonly DispatcherTimer _doneTimer;
        private readonly SemaphoreSlim _metaGate = new(3);
        private readonly ThumbnailWindow<PackTexture> _thumbs;
        private readonly bool _viewConstructed;

        private enum StatusKind { Error, Success, Warning }

        private bool _loadedOnce;
        private bool _applyingSettings;
        private List<PackVersion> _versions = new();
        private bool _busy;
        private int _syncRun;
        private string? _iconPath;
        private string? _lastOutput;
        private string _lastSyncedName = "";
        private CancellationTokenSource? _runCts;

        public TexturePackView()
        {
            InitializeComponent();
            TextureList.ItemsSource = _textures;
            _thumbs = new(TextureList, () => _textures, i => i.Thumbnail, (i, v) => i.Thumbnail = (System.Windows.Media.Imaging.BitmapSource?)v,
                (i, token) => Task.Run(() => (object?)SpritePackerManager.LoadThumbnail(i.FullPath, 80), token));
            InputCard.ItemsSource = _sources;
            InputCard.FileFilterProvider = () => Loc.T("Str_TexturePack_MsgPngFilter", "PNG pictures|*.png|All files|*.*");
            _sources.CollectionChanged += (_, _) => InputCard.SetEmpty(_sources.Count == 0);
            _textures.CollectionChanged += (_, _) => UpdateListState();

            _versions = TexturePackVersions.Load();
            CmbVersion.ItemsSource = _versions;
            CmbVersion.SelectedIndex = 0;

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            _validateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); Revalidate(); };
            _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); UiMotion.FadeVisibility(DonePill, false); };

            _viewConstructed = true;
        }

        // =================================================================
        //  Start-up, settings
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            // The versions may have been updated in Settings while this page was away.
            if (_loadedOnce) { RefreshVersions((CmbVersion.SelectedItem as PackVersion)?.Label); return; }
            _loadedOnce = true;
            ApplySettings(TexturePackManager.LoadSettings());
            UpdateListState();
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSettings(); }
        }

        private void ApplySettings(TexturePackSettings s)
        {
            _applyingSettings = true;
            try
            {
                TxtPackName.Text = s.PackName;
                TxtDescription.Text = s.Description;
                var version = _versions.FirstOrDefault(v => v.Label == s.VersionLabel) ?? _versions[0];
                CmbVersion.SelectedItem = version;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                (s.AsZip ? RadioZip : RadioFolder).IsChecked = true;
                OutputCard.FileName = s.PackName;
                _lastSyncedName = s.PackName;
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdateVersionHint();
        }

        private void SaveSettings() => TexturePackManager.SaveSettings(new TexturePackSettings
        {
            PackName = TxtPackName.Text,
            Description = TxtDescription.Text,
            VersionLabel = (CmbVersion.SelectedItem as PackVersion)?.Label ?? "",
            IncludeSubfolders = InputCard.IncludeSubfolders,
            AsZip = RadioZip.IsChecked == true,
        });

        private void QueueSave()
        {
            if (!_viewConstructed || _applyingSettings) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Setting_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _applyingSettings) return;
            // The file name follows the pack name until the user changes the file name themselves.
            if (ReferenceEquals(sender, TxtPackName) && OutputCard.FileName == _lastSyncedName)
            {
                OutputCard.FileName = TxtPackName.Text;
                _lastSyncedName = TxtPackName.Text;
            }
            QueueSave();
        }

        private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateVersionHint();
            QueueSave();
        }

        private void Setting_Click(object sender, RoutedEventArgs e) => QueueSave();

        private void UpdateVersionHint()
        {
            if (CmbVersion.SelectedItem is not PackVersion v) { TxtVersionHint.Text = ""; return; }
            TxtVersionHint.Text = v.UsesMinMax
                ? Loc.F("Str_TexturePack_MsgWritesMinMax", "Writes min_format and max_format {0}.", v.Format)
                : Loc.F("Str_TexturePack_MsgWritesPackFormat", "Writes pack_format {0}.", v.Format);
        }

        // =================================================================
        //  Keeping the Minecraft version list up to date
        //  (looking for new releases and updating from the wiki live in Settings; the list is read again whenever this page opens)
        // =================================================================

        /// <summary>Reloads the list (built-in + the user's file) and keeps the choice when it still exists.</summary>
        private void RefreshVersions(string? selectLabel)
        {
            _applyingSettings = true;
            try
            {
                _versions = TexturePackVersions.Load();
                CmbVersion.ItemsSource = _versions;
                CmbVersion.SelectedItem = _versions.FirstOrDefault(v => v.Label == selectLabel) ?? _versions[0];
            }
            finally
            {
                _applyingSettings = false;
            }
            UpdateVersionHint();
            QueueSave();
        }

        private void BtnVersionSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsView.RequestedSection = "TexturePack";
            ShortcutManager.OpenToolRequested?.Invoke(ToolCatalogManager.SettingsId);
        }

        // =================================================================
        //  The "Add a Minecraft version" dialog (inside the page, not a window)
        // =================================================================

        private void BtnAddVersion_Click(object sender, RoutedEventArgs e)
        {
            TxtMvLabel.Text = "";
            TxtMvFormat.Text = "";
            ChkMvNewStyle.IsChecked = true;
            TxtMvError.Visibility = Visibility.Collapsed;
            MainLayout.IsEnabled = false;
            ModalHost.Visibility = Visibility.Visible;
            Dispatcher.InvokeAsync(() => TxtMvLabel.Focus(), DispatcherPriority.Input);
        }

        private void CloseVersionDialog()
        {
            ModalHost.Visibility = Visibility.Collapsed;
            MainLayout.IsEnabled = true;
        }

        private void BtnMvCancel_Click(object sender, RoutedEventArgs e) => CloseVersionDialog();

        private void BtnMvAdd_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtMvLabel.Text.Trim();
            if (name.Length == 0 || name.Length > 60)
            {
                ShowVersionError(Loc.T("Str_TexturePack_MsgTypeVersion", "Type the version, for example 26.4."));
                TxtMvLabel.Focus();
                return;
            }
            if (!int.TryParse(TxtMvFormat.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int format) || format is < 1 or > 100000)
            {
                ShowVersionError(Loc.T("Str_TexturePack_MsgWholeNumber", "The format number must be a whole number, for example 97."));
                TxtMvFormat.Focus();
                return;
            }
            var version = new PackVersion(name, format, ChkMvNewStyle.IsChecked == true);
            TexturePackVersions.AddOrUpdate(version);
            CloseVersionDialog();
            RefreshVersions(version.Label);
            ShowStatus(Loc.F("Str_TexturePack_MsgVersionAdded", "Added {0} (format {1}) to your list.", version.Label, version.Format), StatusKind.Success);
        }

        private void ShowVersionError(string text)
        {
            TxtMvError.Text = text;
            TxtMvError.Visibility = Visibility.Visible;
        }

        private void Modal_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseVersionDialog();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.FocusedElement is TextBox)
            {
                BtnMvAdd_Click(sender, e);
                e.Handled = true;
            }
        }

        // =================================================================
        //  Pictures
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            if (_busy) return;
            foreach (string path in e.Paths)
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && !(File.Exists(path) && string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))) continue;
                if (_sources.Any(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _sources.Add(new TextureSource { FullPath = path, IsFolder = isFolder });
            }
            _ = ResyncAsync();
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e)
        {
            QueueSave();
            _ = ResyncAsync();
        }

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_busy) return;
            _syncRun++;
            _sources.Clear();
            _textures.Clear();
            _thumbs.Reset();
            HideStatus();
            ScheduleValidate();
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || (sender as FrameworkElement)?.Tag is not TextureSource source) return;
            _sources.Remove(source);
            _ = ResyncAsync();
        }

        /// <summary>Makes the list match the sources: new pictures are added (keeping any slot already typed), gone ones removed.</summary>
        private async Task ResyncAsync()
        {
            int run = ++_syncRun;
            var paths = _sources.Select(s => s.FullPath).ToList();
            bool include = InputCard.IncludeSubfolders;
            var (files, inaccessible) = await Task.Run(() => TexturePackManager.FindPngs(paths, include));
            if (run != _syncRun) return;

            bool capped = files.Count > TexturePackManager.MaxTextures;
            if (capped) files = files.Take(TexturePackManager.MaxTextures).ToList();
            var wanted = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

            for (int i = _textures.Count - 1; i >= 0; i--)
                if (!wanted.Contains(_textures[i].FullPath)) _textures.RemoveAt(i);

            var have = new HashSet<string>(_textures.Select(t => t.FullPath), StringComparer.OrdinalIgnoreCase);
            var added = new List<PackTexture>();
            foreach (string file in files)
            {
                if (have.Contains(file)) continue;
                var texture = new PackTexture { FullPath = file, Slot = TexturePackManager.GuessSlot(file) };
                _textures.Add(texture);
                added.Add(texture);
            }

            foreach (var source in _sources)
            {
                if (!source.IsFolder) continue;
                string prefix = source.FullPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                source.Count = files.Count(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }
            InputCard.ItemsSource = null;
            InputCard.ItemsSource = _sources;

            if (inaccessible > 0)
                ShowStatus(Loc.F("Str_TexturePack_MsgInaccessible", "{0} folder(s) couldn't be read and were skipped.", inaccessible), StatusKind.Warning);
            else if (capped)
                ShowStatus(Loc.F("Str_TexturePack_MsgListFull", "The list is full ({0:N0} pictures).", TexturePackManager.MaxTextures), StatusKind.Warning);
            else if (_sources.Count > 0 && files.Count == 0)
                ShowStatus(Loc.T("Str_TexturePack_MsgNoPngFound", "No PNG pictures were found there."), StatusKind.Warning);
            else HideStatus();

            ScheduleValidate();
            LoadMeta(added);
            _thumbs.Refresh();
        }

        /// <summary>Sizes and a small thumbnail, a few at a time, so adding a big folder never freezes the page.</summary>
        private async void LoadMeta(IReadOnlyList<PackTexture> items)
        {
            foreach (var item in items)
            {
                await _metaGate.WaitAsync();
                _ = Task.Run(() =>
                {
                    try
                    {
                        bool ok = SpritePackerManager.TryReadSize(item.FullPath, out int w, out int h);
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (!_textures.Contains(item)) return;
                            if (ok) { item.Width = w; item.Height = h; }
                            ScheduleValidate();
                        });
                    }
                    finally
                    {
                        _metaGate.Release();
                    }
                });
            }
        }

        // =================================================================
        //  Checking
        // =================================================================

        private void ScheduleValidate()
        {
            _validateTimer.Stop();
            _validateTimer.Start();
        }

        private void Revalidate()
        {
            TexturePackManager.Validate(_textures.ToList());
            UpdateListState();
        }

        private void UpdateListState()
        {
            int total = _textures.Count;
            int usable = _textures.Count(t => t.IsChecked && t.State != PackTextureState.Error);
            int warnings = _textures.Count(t => t.IsChecked && t.State == PackTextureState.Warning);
            int errors = _textures.Count(t => t.IsChecked && t.State == PackTextureState.Error);

            ListEmpty.Visibility = total > 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtPillCount.Text = Loc.F("Str_TexturePack_MsgCount", "{0} of {1} textures in the pack", usable, total);
            PillIssues.Visibility = warnings + errors > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtPillIssues.Text = string.Join(" · ", new[]
            {
                errors > 0 ? Loc.F("Str_TexturePack_MsgLeftOut", "{0} left out (see the red icon)", errors) : "",
                warnings > 0 ? Loc.F("Str_TexturePack_MsgToCheck", "{0} to check", warnings) : "",
            }.Where(t => t.Length > 0));
            TxtActionInfo.Text = usable > 0
                ? Loc.N(usable, "Str_TexturePack_MsgWrittenOne", "{0} texture will be written to assets/minecraft/textures.", "Str_TexturePack_MsgWrittenMany", "{0} textures will be written to assets/minecraft/textures.", usable)
                : Loc.T("Str_TexturePack_MsgAddPictures", "Add pictures and give each one a texture name to build a pack.");
            if (!_busy) BtnRun.IsEnabled = usable > 0;
        }

        private void TextureCheck_Click(object sender, RoutedEventArgs e) => ScheduleValidate();

        private void SlotBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox { DataContext: PackTexture texture } box) return;
            texture.Slot = TexturePackManager.NormalizeSlot(box.Text);
            ScheduleValidate();
        }

        /// <summary>Delete takes the selected pictures out of the pack (they stay on the list, unticked).</summary>
        private void TextureList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete || Keyboard.FocusedElement is TextBox) return;
            foreach (var t in TextureList.SelectedItems.Cast<PackTexture>().ToList()) t.IsChecked = false;
            ScheduleValidate();
            e.Handled = true;
        }

        // =================================================================
        //  Pack icon
        // =================================================================

        private void BtnChooseIcon_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = Loc.T("Str_TexturePack_MsgIconTitle", "Pack icon (pack.png)"), Filter = Loc.T("Str_TexturePack_MsgPngOne", "PNG picture|*.png") };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            _iconPath = dialog.FileName;
            TxtIconName.Text = Path.GetFileName(_iconPath);
            BtnClearIcon.Visibility = Visibility.Visible;
        }

        private void BtnClearIcon_Click(object sender, RoutedEventArgs e)
        {
            _iconPath = null;
            TxtIconName.Text = Loc.T("Str_TexturePack_MsgChoosePng", "Choose a PNG");
            BtnClearIcon.Visibility = Visibility.Collapsed;
        }

        // =================================================================
        //  Creating the pack
        // =================================================================

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (CmbVersion.SelectedItem is not PackVersion version) return;

            Revalidate();
            var items = TexturePackManager.Exportable(_textures);
            if (items.Count == 0 && _iconPath == null)
            {
                ShowStatus(Loc.T("Str_TexturePack_MsgNothingYet", "There is nothing to put in the pack yet - add pictures and give each a texture name."), StatusKind.Warning);
                return;
            }

            string? path = OutputCard.GetSavePath(Loc.T("Str_TexturePack_MsgZipFilter", "Zip file|*.zip"), out bool overwriteAllowed, out string? problem);
            if (path == null)
            {
                if (problem != null) ShowStatus(Loc.F("Str_TexturePack_MsgCantSaveThere", "Can't save there: {0}", problem), StatusKind.Error);
                return;
            }

            bool asZip = RadioZip.IsChecked == true;
            string description = TxtDescription.Text;
            string? icon = _iconPath != null && File.Exists(_iconPath) ? _iconPath : null;
            var snapshot = _textures.ToList();

            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            SetBusy(true);
            var result = await Task.Run(() => TexturePackManager.Save(path, overwriteAllowed, asZip, description, version, snapshot, icon, token));
            SetBusy(false);

            if (result.Problem != null)
            {
                ShowStatus(Loc.F("Str_TexturePack_MsgCreateFailed", "Couldn't create the pack: {0}", result.Problem), StatusKind.Error);
                return;
            }
            HideStatus();
            _lastOutput = result.Path;
            BtnOpenResult.Visibility = _lastOutput != null ? Visibility.Visible : Visibility.Collapsed;
            ShowDone(Loc.N(result.Written, "Str_TexturePack_MsgSavedOne", "Saved {0} texture", "Str_TexturePack_MsgSavedMany", "Saved {0} textures", result.Written));
            if (result.Skipped > 0)
                ShowStatus(Loc.F("Str_TexturePack_MsgSkipped", "{0} picture(s) were left out (unticked, no valid name, or a repeated name).", result.Skipped), StatusKind.Warning);
            OutputCard.Refresh();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            OutputCard.IsLocked = busy;
            InputCard.IsLocked = busy;
            SidebarStack.IsEnabled = !busy;
            ListArea.IsEnabled = !busy;
            BtnRun.IsEnabled = !busy && TexturePackManager.Exportable(_textures).Count > 0;
        }

        private void BtnOpenResult_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOutput == null) return;
            try
            {
                if (Directory.Exists(_lastOutput)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutput}\"") { UseShellExecute = true });
                else if (File.Exists(_lastOutput)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowStatus(Loc.F("Str_TexturePack_MsgOpenFailed", "Couldn't open it: {0}", ex.Message), StatusKind.Error);
            }
        }

        // =================================================================
        //  Messages
        // =================================================================

        private void ShowDone(string text)
        {
            TxtDonePill.Text = text;
            DonePill.Visibility = Visibility.Visible;
            UiMotion.Pop(DonePill);
            _doneTimer.Stop();
            _doneTimer.Start();
        }

        private void ShowStatus(string text, StatusKind kind)
        {
            var (bg, fg, icon) = kind switch
            {
                StatusKind.Error => ("StatusErrorBg", "StatusErrorFg", PackIconKind.AlertCircleOutline),
                StatusKind.Warning => ("StatusWarningBg", "StatusWarningFg", PackIconKind.AlertOutline),
                _ => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.CheckCircleOutline),
            };
            StatusBanner.SetResourceReference(Border.BackgroundProperty, bg);
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, fg);
            StatusIcon.SetResourceReference(PackIcon.ForegroundProperty, fg);
            StatusIcon.Kind = icon;
            TxtStatus.Text = text;
            StatusBanner.Visibility = Visibility.Visible;
            UiMotion.FadeIn(StatusBanner);
        }

        private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

        // =================================================================
        //  Shell hook
        // =================================================================

        /// <summary>Called when the window closes: stops a running save and keeps the setup.</summary>
        public void CancelActiveRun()
        {
            _thumbs.Dispose();
            _syncRun++;
            _validateTimer.Stop();
            _doneTimer.Stop();
            _runCts?.Cancel();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                if (_loadedOnce) SaveSettings();
            }
        }
    }
}
