using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Modules;
using System.Windows.Data;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Settings view - UI only. Data lives in <see cref="AppSettingsStore"/>;
    /// checks and clean-up in <see cref="SettingsManager"/>. Every change is
    /// saved straight away (the old view previewed a theme without saving it,
    /// so the app looked different from what it would start with).
    /// </summary>
    public partial class SettingsView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DialogTitle = "Settings";

        private readonly DispatcherTimer _savedTimer;
        private readonly DispatcherTimer _testTimer;
        private bool _optionsCollapsed;
        private bool _applying;
        private readonly bool _viewConstructed;

        public SettingsView()
        {
            InitializeComponent();
            PanelClipboard.SettingChanged += SaveAndConfirm;
            PanelTextRecognition.SettingChanged += SaveAndConfirm;
            PanelTranslate.SettingChanged += SaveAndConfirm;

            CategoriesList.ItemsSource = AppSettingsStore.FileCategories;
            PatternsList.ItemsSource = AppSettingsStore.SavedPatterns;

            AppSettingsStore.FileCategories.CollectionChanged += Categories_CollectionChanged;
            foreach (var c in AppSettingsStore.FileCategories) c.PropertyChanged += Category_PropertyChanged;
            AppSettingsStore.SavedPatterns.CollectionChanged += Patterns_CollectionChanged;

            MockListsList.ItemsSource = AppSettingsStore.MockLists;
            AppSettingsStore.MockLists.CollectionChanged += MockLists_CollectionChanged;
            foreach (var l in AppSettingsStore.MockLists) l.PropertyChanged += MockList_PropertyChanged;

            EventDaysList.ItemsSource = AppSettingsStore.EventDays;
            AppSettingsStore.EventDays.CollectionChanged += EventDays_CollectionChanged;
            foreach (var d in AppSettingsStore.EventDays) d.PropertyChanged += EventDay_PropertyChanged;

            _categoriesView = CollectionViewSource.GetDefaultView(AppSettingsStore.FileCategories);
            _categoriesView.Filter = o => o is FileCategory c
                && Matches(TxtCategorySearch, c.Name, c.Extensions);
            _patternsView = CollectionViewSource.GetDefaultView(AppSettingsStore.SavedPatterns);
            _patternsView.Filter = o => o is SavedSearchPattern p
                && Matches(TxtPatternSearch, p.Name, p.Pattern);
            _mockListsView = CollectionViewSource.GetDefaultView(AppSettingsStore.MockLists);
            _mockListsView.Filter = o => o is MockDataList l
                && Matches(TxtMockListSearch, l.Name, l.Values);
            _eventDaysView = CollectionViewSource.GetDefaultView(AppSettingsStore.EventDays);
            _eventDaysView.Filter = o => o is EventDay d
                && Matches(TxtEventDaySearch, d.Name, d.Date);

            _savedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            _savedTimer.Tick += (_, _) =>
            {
                _savedTimer.Stop();
                UiMotion.FadeVisibility(SavedPill, false);
            };
            _testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _testTimer.Tick += (_, _) =>
            {
                _testTimer.Stop();
                UpdatePatternTests();
            };

            // Only reflects the stored values - applying the theme/language
            // here (as the old SelectionChanged handlers did while the page was
            // being built) flipped the whole app to Light/English and back.
            LoadGeneralSettings();

            ShortcutManager.RefreshTitles();
            _shortcutsView = new ListCollectionView(ShortcutManager.Bindings) { Filter = ShortcutMatches };
            _shortcutsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Smart_Tools.WPF.Models.ShortcutBinding.GroupTitle)));
            _shortcutsView.SortDescriptions.Add(new SortDescription(nameof(Smart_Tools.WPF.Models.ShortcutBinding.SortKey), ListSortDirection.Ascending));
            _shortcutsView.SortDescriptions.Add(new SortDescription(nameof(Smart_Tools.WPF.Models.ShortcutBinding.Title), ListSortDirection.Ascending));
            ShortcutsList.ItemsSource = _shortcutsView;
            ShortcutsList.AddHandler(KeyRecorderBox.ChordChangedEvent, new RoutedEventHandler(ShortcutChordChanged));
            ShortcutsList.AddHandler(KeyRecorderBox.ChordRejectedEvent, new RoutedEventHandler(ShortcutChordRejected));

            _viewConstructed = true;
        }

        private readonly ListCollectionView _shortcutsView;
        private readonly ICollectionView _categoriesView;
        private readonly ICollectionView _patternsView;
        private readonly ICollectionView _mockListsView;
        private readonly ICollectionView _eventDaysView;

        private static bool Matches(TextBox? search, params string?[] fields)
        {
            string text = search?.Text.Trim() ?? "";
            if (text.Length == 0) return true;
            foreach (var f in fields)
                if (f != null && f.Contains(text, StringComparison.CurrentCultureIgnoreCase)) return true;
            return false;
        }

        private void ListSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Ignore) return;
            if (ReferenceEquals(sender, TxtCategorySearch)) _categoriesView.Refresh();
            else if (ReferenceEquals(sender, TxtPatternSearch)) _patternsView.Refresh();
            else if (ReferenceEquals(sender, TxtMockListSearch)) _mockListsView.Refresh();
            else if (ReferenceEquals(sender, TxtEventDaySearch)) _eventDaysView.Refresh();
            UpdateEmptyStates();
        }

        /// <summary>Called from MainWindow.Window_Closing: stops timers and saves anything typed but not yet saved.</summary>
        public void CancelActiveRun()
        {
            _testTimer.Stop();
            _savedTimer.Stop();
            AppSettingsStore.Save();
        }

        // =================================================================
        //  Password vault (logic: PasswordVaultManager settings file)
        // =================================================================

        private void LoadVaultSettings()
        {
            _applying = true;
            try
            {
                if (CmbVaultAutoLock.Items.Count == 0)
                {
                    foreach (int m in PasswordVaultManager.AutoLockChoices)
                        CmbVaultAutoLock.Items.Add(new ComboBoxItem { Tag = m, Content = m == 0 ? Str("Str_Vault_Never", "Never") : string.Format(Str("Str_Vault_Minutes", "{0} min"), m) });
                }
                if (CmbVaultClipboard.Items.Count == 0)
                {
                    foreach (int s in PasswordVaultManager.ClipboardChoices)
                        CmbVaultClipboard.Items.Add(new ComboBoxItem { Tag = s, Content = string.Format(Str("Str_Vault_Seconds", "{0} s"), s) });
                }
                var settings = PasswordVaultManager.LoadSettings();
                CmbVaultAutoLock.SelectedIndex = Math.Max(0, Array.IndexOf(PasswordVaultManager.AutoLockChoices, settings.AutoLockMinutes));
                int clip = Array.IndexOf(PasswordVaultManager.ClipboardChoices, settings.ClipboardSeconds);
                CmbVaultClipboard.SelectedIndex = clip >= 0 ? clip : Array.IndexOf(PasswordVaultManager.ClipboardChoices, 30);
                ChkVaultLockMinimize.IsChecked = settings.LockOnMinimize;
                ChkVaultLockSession.IsChecked = settings.LockOnSessionLock;
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>Reads the vault settings file, lets <paramref name="change"/> edit it, saves it. An open vault view picks it up through SettingsSaved.</summary>
        private void ChangeVaultSettings(Func<Smart_Tools.WPF.Models.VaultSettings, bool> change)
        {
            var settings = PasswordVaultManager.LoadSettings();
            if (!change(settings)) return;
            PasswordVaultManager.SaveSettings(settings);
            SaveAndConfirm();
        }

        private void CmbVaultAutoLock_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Ignore || CmbVaultAutoLock.SelectedItem is not ComboBoxItem { Tag: int minutes }) return;
            ChangeVaultSettings(s => { bool differs = s.AutoLockMinutes != minutes; s.AutoLockMinutes = minutes; return differs; });
        }

        private void CmbVaultClipboard_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Ignore || CmbVaultClipboard.SelectedItem is not ComboBoxItem { Tag: int seconds }) return;
            ChangeVaultSettings(s => { bool differs = s.ClipboardSeconds != seconds; s.ClipboardSeconds = seconds; return differs; });
        }

        private void ChkVaultLockMinimize_Click(object sender, RoutedEventArgs e)
        {
            bool on = ChkVaultLockMinimize.IsChecked == true;
            ChangeVaultSettings(s => { s.LockOnMinimize = on; return true; });
        }

        private void ChkVaultLockSession_Click(object sender, RoutedEventArgs e)
        {
            bool on = ChkVaultLockSession.IsChecked == true;
            ChangeVaultSettings(s => { s.LockOnSessionLock = on; return true; });
        }

        // =================================================================
        //  Media Viewer (logic: MediaViewerManager settings file)
        // =================================================================

        private void LoadMediaViewerSettings()
        {
            _applying = true;
            try
            {
                var s = MediaViewerManager.LoadSettings();
                ChkMvWidgetStartup.IsChecked = s.ShowWidgetAtStartup;
                ChkMvKeepPlaying.IsChecked = s.KeepPlayingWithWidget;
                ChkMvCompact.IsChecked = s.WidgetCompact;
                ChkMvRemember.IsChecked = s.RememberPlaylist;
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>Changes only the Settings page's fields of the Media Viewer file. An open Media Viewer picks it up through SettingsChanged.</summary>
        private void ChangeMediaViewerSettings(Action<Smart_Tools.WPF.Models.MediaViewerSettings> change)
        {
            MediaViewerManager.UpdateSettings(change);
            SaveAndConfirm();
        }

        private void ChkMvWidgetStartup_Click(object sender, RoutedEventArgs e)
        {
            bool on = ChkMvWidgetStartup.IsChecked == true;
            ChangeMediaViewerSettings(s => s.ShowWidgetAtStartup = on);
        }

        private void ChkMvKeepPlaying_Click(object sender, RoutedEventArgs e)
        {
            bool on = ChkMvKeepPlaying.IsChecked == true;
            ChangeMediaViewerSettings(s => s.KeepPlayingWithWidget = on);
        }

        private void ChkMvCompact_Click(object sender, RoutedEventArgs e)
        {
            bool on = ChkMvCompact.IsChecked == true;
            ChangeMediaViewerSettings(s => s.WidgetCompact = on);
        }

        /// <summary>Turning "remember" off also drops what was saved, so nothing old comes back if it's turned on later.</summary>
        private void ChkMvRemember_Click(object sender, RoutedEventArgs e)
        {
            bool on = ChkMvRemember.IsChecked == true;
            ChangeMediaViewerSettings(s =>
            {
                s.RememberPlaylist = on;
                if (!on)
                {
                    s.Sources = new();
                    s.LastItem = "";
                }
            });
        }

        private void BtnMvForget_Click(object sender, RoutedEventArgs e) =>
            ChangeMediaViewerSettings(s =>
            {
                s.Sources = new();
                s.LastItem = "";
            });

        private void BtnMvResetWidget_Click(object sender, RoutedEventArgs e) =>
            ChangeMediaViewerSettings(s =>
            {
                s.WidgetLeft = null;
                s.WidgetTop = null;
            });

        /// <summary>Set by another page before opening Settings, to land on one section ("TextRecognition"). Cleared once used.</summary>
        public static string? RequestedSection { get; set; }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            LoadVaultSettings();
            TxtSettingsPath.Text = AppSettingsStore.SettingsFilePath;
            TxtSettingsPath.ToolTip = AppSettingsStore.SettingsFilePath;
            TxtLoadProblem.Text = AppSettingsStore.LoadProblem ?? "";
            LoadProblemBanner.Visibility = AppSettingsStore.LoadProblem != null ? Visibility.Visible : Visibility.Collapsed;
            RefreshCategoryWarnings();
            RefreshMockListWarnings();
            RefreshEventDayWarnings();
            ValidateAllPatterns();
            UpdateEmptyStates();
            LoadShortcutSettings();

            if (RequestedSection == "TextRecognition")
            {
                BtnNavTextRecognition.IsChecked = true;
                Nav_Click(BtnNavTextRecognition, new RoutedEventArgs());
            }
            else if (RequestedSection == "Translate")
            {
                BtnNavTranslate.IsChecked = true;
                Nav_Click(BtnNavTranslate, new RoutedEventArgs());
            }
            RequestedSection = null;
        }

        private bool Ignore => !_viewConstructed || _applying;

        // =================================================================
        //  Sidebar
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            LblNavGeneral.Visibility = visibility;
            LblNavCategories.Visibility = visibility;
            LblNavPatterns.Visibility = visibility;
            LblNavMockData.Visibility = visibility;
            LblNavEventDays.Visibility = visibility;
            LblNavShortcuts.Visibility = visibility;
            LblNavVault.Visibility = visibility;
            LblNavMediaViewer.Visibility = visibility;
            LblNavClipboard.Visibility = visibility;
            LblNavTextRecognition.Visibility = visibility;
            LblNavTranslate.Visibility = visibility;
            SidebarContent.Margin = _optionsCollapsed ? new Thickness(4, 4, 4, 16) : new Thickness(12, 4, 12, 16);

            var padding = _optionsCollapsed ? new Thickness(0, 10, 0, 10) : new Thickness(14, 10, 14, 10);
            var alignment = _optionsCollapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
            foreach (var nav in new[] { BtnNavGeneral, BtnNavCategories, BtnNavPatterns, BtnNavMockData, BtnNavEventDays, BtnNavShortcuts, BtnNavVault, BtnNavMediaViewer, BtnNavClipboard, BtnNavTextRecognition, BtnNavTranslate })
            {
                nav.Padding = padding;
                nav.HorizontalContentAlignment = alignment;
            }
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        /// <summary>Click, not Checked: Checked also fires while the page is built.</summary>
        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            string section = (sender as RadioButton)?.Tag as string ?? "General";
            ShowSection(PanelGeneral, section == "General");
            ShowSection(PanelCategories, section == "Categories");
            ShowSection(PanelPatterns, section == "Patterns");
            ShowSection(PanelMockData, section == "MockData");
            ShowSection(PanelEventDays, section == "EventDays");
            ShowSection(PanelShortcuts, section == "Shortcuts");
            ShowSection(PanelVault, section == "Vault");
            ShowSection(PanelMediaViewer, section == "MediaViewer");
            if (section == "MediaViewer") LoadMediaViewerSettings();   // the Media Viewer may have saved since the page was built
            ShowSection(PanelClipboard, section == "Clipboard");
            ShowSection(PanelTextRecognition, section == "TextRecognition");
            if (section == "TextRecognition") PanelTextRecognition.Load();   // languages may have been added since the page was built
            ShowSection(PanelTranslate, section == "Translate");
            if (section == "Translate") PanelTranslate.Load();
            if (section == "Vault") LoadVaultSettings();   // the vault sidebar may have saved since the page was built
            ContentScroller.ScrollToTop();
        }

        // =================================================================
        //  Keyboard shortcuts (logic: ShortcutManager)
        // =================================================================

        private void LoadShortcutSettings()
        {
            _applying = true;
            try
            {
                ChkGlobalHotkeys.IsChecked = AppSettingsStore.GlobalHotkeysEnabled;
                ChkCloseToTray.IsChecked = AppSettingsStore.CloseToTray;
                ChkStartWithWindows.IsChecked = AppSettingsStore.StartWithWindows;
            }
            finally
            {
                _applying = false;
            }

            PanelClipboard.Load();
            PanelTextRecognition.Load();
            PanelTranslate.Load();
            ShortcutManager.RefreshTitles();   // the language may have changed since the page was built
            _shortcutsView.Refresh();
            UpdateShortcutsEmpty();
        }

        private bool ShortcutMatches(object item)
        {
            string text = TxtShortcutSearch?.Text.Trim() ?? "";
            if (text.Length == 0 || item is not Smart_Tools.WPF.Models.ShortcutBinding b) return true;
            return b.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                   || b.GroupTitle.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                   || b.ChordText.Contains(text, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateShortcutsEmpty() =>
            ShortcutsEmpty.Visibility = _shortcutsView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

        private void TxtShortcutSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Ignore) return;
            _shortcutsView.Refresh();
            UpdateShortcutsEmpty();
        }

        private void ChkGlobalHotkeys_Click(object sender, RoutedEventArgs e)
        {
            ShortcutManager.SetGlobalHotkeysEnabled(ChkGlobalHotkeys.IsChecked == true);
            SaveAndConfirm();
        }

        private void ChkCloseToTray_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsStore.CloseToTray = ChkCloseToTray.IsChecked == true;
            SaveAndConfirm();
        }

        private void ChkStartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsStore.StartWithWindows = ChkStartWithWindows.IsChecked == true;
            ShortcutManager.ApplyStartWithWindows(AppSettingsStore.StartWithWindows);
            SaveAndConfirm();
        }

        private void ShortcutChordChanged(object sender, RoutedEventArgs e)
        {
            TxtShortcutError.Visibility = Visibility.Collapsed;
            SaveAndConfirm();
        }

        private void ShortcutChordRejected(object sender, RoutedEventArgs e)
        {
            string? code = (e.OriginalSource as KeyRecorderBox)?.RejectCode;
            TxtShortcutError.Text = code == "needsModifier"
                ? Str("Str_Shortcuts_NeedsModifier", "A global shortcut needs Ctrl, Alt or Win as well.")
                : Str("Str_Shortcuts_Reserved", "Windows keeps this key for itself. Choose another one.");
            UiMotion.FadeVisibility(TxtShortcutError, true);
        }

        private void ShortcutEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { DataContext: Smart_Tools.WPF.Models.ShortcutBinding b } box) return;
            ShortcutManager.SetEnabled(b, box.IsChecked == true);
            SaveAndConfirm();
        }

        private void BtnResetShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: Smart_Tools.WPF.Models.ShortcutBinding b }) return;
            ShortcutManager.Reset(b);
            TxtShortcutError.Visibility = Visibility.Collapsed;
            SaveAndConfirm();
        }

        private void BtnResetShortcuts_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmRemove(Str("Str_Shortcuts_ResetAllTip", "Put every shortcut back to its default"))) return;
            ShortcutManager.ResetAll();
            TxtShortcutError.Visibility = Visibility.Collapsed;
            SaveAndConfirm();
        }

        private static void ShowSection(FrameworkElement panel, bool show)
        {
            bool wasVisible = panel.Visibility == Visibility.Visible;
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && !wasVisible) UiMotion.FadeIn(panel, 6);
        }

        private void BtnOpenSettingsFolder_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(AppSettingsStore.SettingsDirectory);
            string target = System.IO.File.Exists(AppSettingsStore.SettingsFilePath) ? AppSettingsStore.SettingsFilePath : AppSettingsStore.SettingsDirectory;
            if (ShellExplorer.Show(target) != ShellExplorer.ShowResult.Shown)
                MessageBox.Show($"Couldn't open {AppSettingsStore.SettingsDirectory}.", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SaveAndConfirm()
        {
            AppSettingsStore.Save();
            if (SavedPill.Visibility != Visibility.Visible)
            {
                UiMotion.FadeVisibility(SavedPill, true);
                UiMotion.Pop(SavedPill, 0.9);
            }
            _savedTimer.Stop();
            _savedTimer.Start();
        }

        private bool ConfirmRemove(string what)
        {
            if (!AppSettingsStore.ConfirmBeforeDeleting) return true;
            return MessageBox.Show(what, DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        // =================================================================
        //  General
        // =================================================================

        private void LoadGeneralSettings()
        {
            _applying = true;
            try
            {
                string theme = SettingsManager.NormalizeTheme(AppSettingsStore.Theme);
                foreach (var radio in ThemeChoices.Children.OfType<RadioButton>())
                    radio.IsChecked = (string)radio.Tag == theme;

                string language = SettingsManager.NormalizeLanguage(AppSettingsStore.Language);
                foreach (var radio in LanguageChoices.Children.OfType<RadioButton>())
                    radio.IsChecked = (string)radio.Tag == language;

                ChkConfirmDelete.IsChecked = AppSettingsStore.ConfirmBeforeDeleting;
                ChkShowNotifications.IsChecked = AppSettingsStore.ShowDesktopNotifications;
                ChkTrackActiveTool.IsChecked = AppSettingsStore.TrackActiveTool;
            }
            finally
            {
                _applying = false;
            }
        }

        private void RadioTheme_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore || sender is not RadioButton { Tag: string theme }) return;
            AppSettingsStore.Theme = SettingsManager.NormalizeTheme(theme);
            ThemeManager.Apply(AppSettingsStore.Theme);
            SaveAndConfirm();
        }

        private void RadioLanguage_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore || sender is not RadioButton { Tag: string code }) return;
            AppSettingsStore.Language = SettingsManager.NormalizeLanguage(code);
            LanguageManager.Apply(AppSettingsStore.Language);
            SaveAndConfirm();
        }

        private void Behaviour_Changed(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            AppSettingsStore.ConfirmBeforeDeleting = ChkConfirmDelete.IsChecked == true;
            AppSettingsStore.ShowDesktopNotifications = ChkShowNotifications.IsChecked == true;
            AppSettingsStore.TrackActiveTool = ChkTrackActiveTool.IsChecked == true;
            SaveAndConfirm();
        }

        // =================================================================
        //  Categories
        // =================================================================

        private void Categories_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (FileCategory c in e.OldItems) c.PropertyChanged -= Category_PropertyChanged;
            if (e.NewItems != null) foreach (FileCategory c in e.NewItems) c.PropertyChanged += Category_PropertyChanged;
            if (e.Action == NotifyCollectionChangedAction.Reset)
                foreach (var c in AppSettingsStore.FileCategories)
                {
                    c.PropertyChanged -= Category_PropertyChanged;
                    c.PropertyChanged += Category_PropertyChanged;
                }
            RefreshCategoryWarnings();
            UpdateEmptyStates();
        }

        private void Category_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(FileCategory.Name) or nameof(FileCategory.Extensions)) RefreshCategoryWarnings();
        }

        /// <summary>Overlaps, empty categories and bad names, shown on each card.</summary>
        private void RefreshCategoryWarnings()
        {
            var list = AppSettingsStore.FileCategories.ToList();
            var warnings = SettingsManager.CategoryWarnings(list.Select(c => (c.Name, c.Extensions)).ToList());
            for (int i = 0; i < list.Count; i++)
            {
                string? nameProblem = SettingsManager.ValidateCategoryName(list[i].Name, list.Where((_, j) => j != i).Select(c => c.Name));
                list[i].Warning = nameProblem ?? warnings[i];
            }
        }

        private void TxtNewCategoryName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            string text = TxtNewCategoryName.Text;
            string? problem = text.Length == 0 ? null : SettingsManager.ValidateCategoryName(text, AppSettingsStore.FileCategories.Select(c => c.Name));
            BtnAddCategory.IsEnabled = text.Trim().Length > 0 && problem == null;
            TxtNewCategoryHint.Text = problem ?? "";
            TxtNewCategoryHint.Visibility = problem != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TxtNewCategoryName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnAddCategory.IsEnabled)
            {
                AddCategory();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                TxtNewCategoryName.Text = "";
            }
        }

        private void BtnAddCategory_Click(object sender, RoutedEventArgs e) => AddCategory();

        private void AddCategory()
        {
            string name = TxtNewCategoryName.Text.Trim();
            if (SettingsManager.ValidateCategoryName(name, AppSettingsStore.FileCategories.Select(c => c.Name)) != null) return;

            // Above "Other" so it gets a chance before the catch-all.
            var category = new FileCategory { Name = name, Extensions = "" };
            int other = AppSettingsStore.FileCategories.ToList().FindIndex(c => string.Equals(c.Name.Trim(), "Other", StringComparison.OrdinalIgnoreCase));
            if (other >= 0) AppSettingsStore.FileCategories.Insert(other, category);
            else AppSettingsStore.FileCategories.Add(category);

            TxtNewCategoryName.Text = "";
            SaveAndConfirm();
            FocusExtensionBoxOf(category);
        }

        /// <summary>After adding a category, put the cursor in its "+ add" box.</summary>
        private void FocusExtensionBoxOf(FileCategory category)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (CategoriesList.ItemContainerGenerator.ContainerFromItem(category) is not DependencyObject container) return;
                var box = FindChildren<TextBox>(container).FirstOrDefault(t => t.Style == TryFindResource("BareTextBox"));
                if (box == null) return;
                box.BringIntoView();
                box.Focus();
            });
        }

        private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (var nested in FindChildren<T>(child)) yield return nested;
            }
        }

        /// <summary>The category a control inside a card belongs to (chip buttons have the extension as their DataContext).</summary>
        private static FileCategory? CategoryOf(DependencyObject? element)
        {
            for (var d = element; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is FrameworkElement { DataContext: FileCategory c }) return c;
            return null;
        }

        private void CategoryName_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Warnings follow the Name binding (PropertyChanged); nothing else to do per keystroke.
        }

        private void CategoryName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox { DataContext: FileCategory c } && c.Name != c.Name.Trim()) c.Name = c.Name.Trim();
            AppSettingsStore.Save();
        }

        private void NewExtension_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox box || CategoryOf(box) is not FileCategory category) return;
            string hint = SettingsManager.DescribeAdd(category.Extensions, box.Text);
            box.ToolTip = hint.Length > 0 ? hint : "Type one or more extensions (png, .png, *.png - any separator) and press Enter";
        }

        private void NewExtension_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;
            if (e.Key == Key.Enter)
            {
                AddTypedExtensions(box);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                box.Text = "";
            }
        }

        private void NewExtension_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box && box.Text.Trim().Length > 0) AddTypedExtensions(box);
        }

        private void AddTypedExtensions(TextBox box)
        {
            if (CategoryOf(box) is not FileCategory category) return;
            var merge = SettingsManager.AddExtensions(category.Extensions, box.Text);
            if (merge.Added.Count > 0)
            {
                category.Extensions = merge.Extensions;
                SaveAndConfirm();
            }
            box.Text = "";
        }

        private void BtnRemoveExtension_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string extension } button || CategoryOf(button) is not FileCategory category) return;
            category.Extensions = SettingsManager.RemoveExtension(category.Extensions, extension);
            SaveAndConfirm();
        }

        private void BtnCategoryUp_Click(object sender, RoutedEventArgs e) => MoveCategory(sender, -1);

        private void BtnCategoryDown_Click(object sender, RoutedEventArgs e) => MoveCategory(sender, +1);

        private void MoveCategory(object sender, int delta)
        {
            if (CategoryOf(sender as DependencyObject) is not FileCategory category) return;
            int index = AppSettingsStore.FileCategories.IndexOf(category);
            int target = index + delta;
            if (index < 0 || target < 0 || target >= AppSettingsStore.FileCategories.Count) return;
            AppSettingsStore.FileCategories.Move(index, target);
            SaveAndConfirm();
        }

        private void BtnRemoveCategory_Click(object sender, RoutedEventArgs e)
        {
            if (CategoryOf(sender as DependencyObject) is not FileCategory category) return;
            if (!ConfirmRemove($"Remove the \"{category.Name}\" category? Organize Files won't sort into it any more.")) return;
            AppSettingsStore.FileCategories.Remove(category);
            SaveAndConfirm();
        }

        private void BtnResetCategories_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Replace your categories with the six built-in ones (Documents, Images, Videos, Audio, Archives, Other)?",
                    DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AppSettingsStore.ResetCategories();
            SaveAndConfirm();
        }

        // =================================================================
        //  Patterns
        // =================================================================

        private void Patterns_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            ValidateAllPatterns();
            UpdatePatternTests();
            UpdateEmptyStates();
        }

        private void ValidateAllPatterns()
        {
            foreach (var p in AppSettingsStore.SavedPatterns) p.Error = SettingsManager.ValidatePattern(p.Pattern, p.Type) ?? "";
        }

        private void PatternText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox { DataContext: SavedSearchPattern p }) return;
            p.Error = SettingsManager.ValidatePattern(p.Pattern, p.Type) ?? "";
            ScheduleTest();
        }

        private void PatternType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || sender is not System.Windows.Controls.ComboBox { DataContext: SavedSearchPattern p }) return;
            p.Error = SettingsManager.ValidatePattern(p.Pattern, p.Type) ?? "";
            ScheduleTest();
            // Only save real changes, not the combo being filled in when the list appears.
            if (e.RemovedItems.Count > 0) AppSettingsStore.Save();
        }

        private void AutoSave_LostFocus(object sender, RoutedEventArgs e) => AppSettingsStore.Save();

        private void BtnRemovePattern_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: SavedSearchPattern pattern }) return;
            if (!ConfirmRemove($"Remove the \"{pattern.Name}\" pattern?")) return;
            AppSettingsStore.SavedPatterns.Remove(pattern);
            SaveAndConfirm();
        }

        private void BtnResetPatterns_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Replace your saved patterns with the built-in ones?", DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AppSettingsStore.ResetPatterns();
            SaveAndConfirm();
        }

        // ---------------- New pattern ----------------

        private string NewPatternType => RadioNewWildcard.IsChecked == true ? SettingsManager.PatternWildcard : SettingsManager.PatternRegex;

        private void NewPattern_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateNewPatternValidation();
        }

        private void NewPatternType_Click(object sender, RoutedEventArgs e) => UpdateNewPatternValidation();

        /// <summary>Invalid patterns can't be added (the old dialog saved them anyway).</summary>
        private void UpdateNewPatternValidation()
        {
            string name = TxtNewPatternName.Text.Trim();
            string pattern = TxtNewPatternText.Text;
            string? error = pattern.Length == 0 ? null : SettingsManager.ValidatePattern(pattern, NewPatternType);
            bool duplicate = name.Length > 0 && AppSettingsStore.SavedPatterns.Any(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

            string message;
            string brush;
            string icon;
            if (error != null)
            {
                (message, brush, icon) = (error, "StatusErrorFg", "AlertOutline");
            }
            else if (duplicate)
            {
                (message, brush, icon) = ($"There's already a pattern called \"{name}\".", "StatusWarningFg", "AlertOutline");
            }
            else if (pattern.Length > 0 && name.Length > 0)
            {
                string test = SettingsManager.DescribeMatches(pattern, NewPatternType, TxtPatternTest.Text);
                (message, brush, icon) = ("Looks good" + (test.Length > 0 ? $" · {test} in the test text" : "") + ".", "StatusSuccessFg", "CheckCircleOutline");
            }
            else
            {
                (message, brush, icon) = (NewPatternType == SettingsManager.PatternWildcard
                    ? "Wildcard: * is any text, ? is one character - e.g. *.log or report_??.pdf"
                    : "Give it a name and a pattern.", "TextSecondary", "InformationOutline");
            }

            TxtNewPatternValidation.Text = message;
            TxtNewPatternValidation.SetResourceReference(TextBlock.ForegroundProperty, brush);
            IconNewPatternState.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, brush);
            IconNewPatternState.Kind = Enum.Parse<MaterialDesignThemes.Wpf.PackIconKind>(icon);
            BtnAddPattern.IsEnabled = name.Length > 0 && pattern.Length > 0 && error == null && !duplicate;
        }

        private void NewPattern_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnAddPattern.IsEnabled)
            {
                AddPattern();
                e.Handled = true;
            }
        }

        private void BtnAddPattern_Click(object sender, RoutedEventArgs e) => AddPattern();

        private void AddPattern()
        {
            var pattern = new SavedSearchPattern
            {
                Name = TxtNewPatternName.Text.Trim(),
                Pattern = TxtNewPatternText.Text,
                Type = NewPatternType,
            };
            AppSettingsStore.SavedPatterns.Add(pattern);
            TxtNewPatternName.Text = "";
            TxtNewPatternText.Text = "";
            TxtNewPatternName.Focus();
            SaveAndConfirm();
        }

        // ---------------- Test box ----------------

        private void TxtPatternTest_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            ScheduleTest();
        }

        private void ScheduleTest()
        {
            _testTimer.Stop();
            _testTimer.Start();
        }

        private void UpdatePatternTests()
        {
            string text = TxtPatternTest.Text;
            var sw = Stopwatch.StartNew();
            foreach (var p in AppSettingsStore.SavedPatterns)
                p.TestResult = SettingsManager.DescribeMatches(p.Pattern, p.Type, text);
            if (sw.ElapsedMilliseconds > 500) Log.Info($"Pattern test took {sw.ElapsedMilliseconds} ms");
            UpdateNewPatternValidation();
        }

        private void UpdateEmptyStates()
        {
            SetEmptyState(CategoriesEmpty, CategoriesNoMatch, AppSettingsStore.FileCategories.Count, _categoriesView);
            SetEmptyState(PatternsEmpty, PatternsNoMatch, AppSettingsStore.SavedPatterns.Count, _patternsView);
            SetEmptyState(MockListsEmpty, MockListsNoMatch, AppSettingsStore.MockLists.Count, _mockListsView);
            SetEmptyState(EventDaysEmpty, EventDaysNoMatch, AppSettingsStore.EventDays.Count, _eventDaysView);
        }

        /// <summary>"Nothing here yet" when the list is empty; "nothing matches" when only the search hides everything.</summary>
        private static void SetEmptyState(UIElement empty, UIElement noMatch, int total, ICollectionView view)
        {
            empty.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            noMatch.Visibility = total > 0 && view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        // =================================================================
        //  Event days and holidays
        // =================================================================

        private void EventDays_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (EventDay d in e.OldItems) d.PropertyChanged -= EventDay_PropertyChanged;
            if (e.NewItems != null) foreach (EventDay d in e.NewItems) d.PropertyChanged += EventDay_PropertyChanged;
            if (e.Action == NotifyCollectionChangedAction.Reset)
                foreach (var d in AppSettingsStore.EventDays)
                {
                    d.PropertyChanged -= EventDay_PropertyChanged;
                    d.PropertyChanged += EventDay_PropertyChanged;
                }
            RefreshEventDayWarnings();
            UpdateEmptyStates();
        }

        private void EventDay_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(EventDay.Name) or nameof(EventDay.Date) or nameof(EventDay.Repeats)) RefreshEventDayWarnings();
        }

        /// <summary>A missing name, an unreadable date, or the same event listed twice - shown on each card.</summary>
        private void RefreshEventDayWarnings()
        {
            var days = AppSettingsStore.EventDays.ToList();
            for (int i = 0; i < days.Count; i++)
            {
                var d = days[i];
                string warning = "";
                if (d.Name.Trim().Length == 0) warning = Str("Str_Settings_EventNoName", "Give this day a name.");
                else if (d.ParsedDate is not { } date) warning = Str("Str_Settings_EventBadDate", "Write the date as yyyy-MM-dd, for example 2026-12-25.");
                else if (days.Where((_, j) => j != i).Any(o => o.ParsedDate is { } od && string.Equals(o.Name.Trim(), d.Name.Trim(), StringComparison.OrdinalIgnoreCase) && SameDay(od, date, d.Repeats || o.Repeats)))
                    warning = Str("Str_Settings_EventDuplicate", "This day is already in the list.");
                d.Warning = warning;
            }
        }

        private static bool SameDay(DateTime a, DateTime b, bool ignoreYear) => ignoreYear ? a.Month == b.Month && a.Day == b.Day : a.Date == b.Date;

        private void NewEvent_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            bool hasName = TxtNewEventName.Text.Trim().Length > 0;
            string date = TxtNewEventDate.Text.Trim();
            bool dateOk = DateTime.TryParseExact(date, EventDay.DateFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
            BtnAddEvent.IsEnabled = hasName && dateOk;
            bool showHint = date.Length > 0 && !dateOk;
            TxtNewEventHint.Text = showHint ? Str("Str_Settings_EventBadDate", "Write the date as yyyy-MM-dd, for example 2026-12-25.") : "";
            TxtNewEventHint.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;
        }

        private void NewEvent_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnAddEvent.IsEnabled)
            {
                AddEventDay();
                e.Handled = true;
            }
        }

        private void BtnAddEvent_Click(object sender, RoutedEventArgs e) => AddEventDay();

        private void AddEventDay()
        {
            var day = new EventDay
            {
                Name = TxtNewEventName.Text.Trim(),
                Date = TxtNewEventDate.Text.Trim(),
                Repeats = ChkNewEventRepeats.IsChecked == true,
                NonWorking = ChkNewEventNonWorking.IsChecked == true,
            };
            AppSettingsStore.EventDays.Add(day);
            TxtNewEventName.Text = "";
            TxtNewEventDate.Text = "";
            TxtNewEventName.Focus();
            SaveAndConfirm();
        }

        private void EventDayField_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox { DataContext: EventDay d })
            {
                if (d.Name != d.Name.Trim()) d.Name = d.Name.Trim();
                if (d.Date != d.Date.Trim()) d.Date = d.Date.Trim();
            }
            AppSettingsStore.Save();
        }

        private void EventDayFlag_Click(object sender, RoutedEventArgs e) => SaveAndConfirm();

        private void BtnRemoveEventDay_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: EventDay day }) return;
            if (!ConfirmRemove(string.Format(Str("Str_Settings_RemoveEventConfirm", "Remove \"{0}\" from your event days?"), day.Name))) return;
            AppSettingsStore.EventDays.Remove(day);
            SaveAndConfirm();
        }

        private void BtnResetEventDays_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Str("Str_Settings_ResetEventDaysConfirm", "Replace your event days with the built-in ones (New Year's Day, Labour Day, Christmas Day)?"),
                    DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AppSettingsStore.ResetEventDays();
            SaveAndConfirm();
        }

        // =================================================================
        //  Mock data lists
        // =================================================================

        private string Str(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        private void MockLists_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (MockDataList l in e.OldItems) l.PropertyChanged -= MockList_PropertyChanged;
            if (e.NewItems != null) foreach (MockDataList l in e.NewItems) l.PropertyChanged += MockList_PropertyChanged;
            if (e.Action == NotifyCollectionChangedAction.Reset)
                foreach (var l in AppSettingsStore.MockLists)
                {
                    l.PropertyChanged -= MockList_PropertyChanged;
                    l.PropertyChanged += MockList_PropertyChanged;
                }
            RefreshMockListWarnings();
            UpdateEmptyStates();
        }

        private void MockList_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MockDataList.Name) or nameof(MockDataList.Values)) RefreshMockListWarnings();
        }

        /// <summary>Empty lists and bad or repeated names, shown on each card.</summary>
        private void RefreshMockListWarnings()
        {
            var lists = AppSettingsStore.MockLists.ToList();
            for (int i = 0; i < lists.Count; i++)
            {
                string? nameProblem = MockDataManager.ValidateListName(lists[i].Name, lists.Where((_, j) => j != i).Select(l => l.Name));
                lists[i].Warning = nameProblem ?? (lists[i].ValueList.Count == 0 ? Str("Str_Settings_MockListEmptyWarning", "This list has no values yet - add some below.") : "");
            }
        }

        private static MockDataList? MockListOf(DependencyObject? element)
        {
            for (var d = element; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is FrameworkElement { DataContext: MockDataList l }) return l;
            return null;
        }

        private void TxtNewMockListName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed) return;
            string text = TxtNewMockListName.Text;
            string? problem = text.Length == 0 ? null : MockDataManager.ValidateListName(text, AppSettingsStore.MockLists.Select(l => l.Name));
            BtnAddMockList.IsEnabled = text.Trim().Length > 0 && problem == null;
            TxtNewMockListHint.Text = problem ?? "";
            TxtNewMockListHint.Visibility = problem != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TxtNewMockListName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnAddMockList.IsEnabled)
            {
                AddMockList();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                TxtNewMockListName.Text = "";
            }
        }

        private void BtnAddMockList_Click(object sender, RoutedEventArgs e) => AddMockList();

        private void AddMockList()
        {
            string name = TxtNewMockListName.Text.Trim();
            if (MockDataManager.ValidateListName(name, AppSettingsStore.MockLists.Select(l => l.Name)) != null) return;

            var list = new MockDataList { Name = name, Values = "" };
            AppSettingsStore.MockLists.Add(list);
            TxtNewMockListName.Text = "";
            SaveAndConfirm();

            // Put the cursor in the new list's "+ add" box.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (MockListsList.ItemContainerGenerator.ContainerFromItem(list) is not DependencyObject container) return;
                var box = FindChildren<TextBox>(container).FirstOrDefault(t => t.Style == TryFindResource("BareTextBox"));
                if (box == null) return;
                box.BringIntoView();
                box.Focus();
            });
        }

        private void MockListName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox { DataContext: MockDataList l } && l.Name != l.Name.Trim()) l.Name = l.Name.Trim();
            AppSettingsStore.Save();
        }

        private void NewMockValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;
            if (e.Key == Key.Enter)
            {
                AddTypedMockValues(box);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                box.Text = "";
            }
        }

        private void NewMockValue_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box && box.Text.Trim().Length > 0) AddTypedMockValues(box);
        }

        private void AddTypedMockValues(TextBox box)
        {
            if (MockListOf(box) is not MockDataList list) return;
            var merge = MockDataManager.AddListValues(list.Values, box.Text);
            if (merge.Added > 0)
            {
                list.Values = merge.Values;
                SaveAndConfirm();
            }
            box.Text = "";
        }

        private void BtnRemoveMockValue_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string value } button || MockListOf(button) is not MockDataList list) return;
            list.Values = MockDataManager.RemoveListValue(list.Values, value);
            SaveAndConfirm();
        }

        private void BtnMockListUp_Click(object sender, RoutedEventArgs e) => MoveMockList(sender, -1);

        private void BtnMockListDown_Click(object sender, RoutedEventArgs e) => MoveMockList(sender, +1);

        private void MoveMockList(object sender, int delta)
        {
            if (MockListOf(sender as DependencyObject) is not MockDataList list) return;
            int index = AppSettingsStore.MockLists.IndexOf(list);
            int target = index + delta;
            if (index < 0 || target < 0 || target >= AppSettingsStore.MockLists.Count) return;
            AppSettingsStore.MockLists.Move(index, target);
            SaveAndConfirm();
        }

        private void BtnRemoveMockList_Click(object sender, RoutedEventArgs e)
        {
            if (MockListOf(sender as DependencyObject) is not MockDataList list) return;
            if (!ConfirmRemove(string.Format(Str("Str_Settings_RemoveMockListConfirm", "Remove the \"{0}\" list? Columns already filled from it keep their values."), list.Name))) return;
            AppSettingsStore.MockLists.Remove(list);
            SaveAndConfirm();
        }

        private void BtnResetMockLists_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Str("Str_Settings_ResetMockListsConfirm", "Replace your Mock Data lists with the built-in ones (Departments, Order status, Payment method, Priority)?"),
                    DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AppSettingsStore.ResetMockLists();
            SaveAndConfirm();
        }
    }
}
