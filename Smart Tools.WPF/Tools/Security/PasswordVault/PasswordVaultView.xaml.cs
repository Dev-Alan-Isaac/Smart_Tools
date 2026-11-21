using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using DataObject = System.Windows.DataObject;
using KeyboardEventArgs = System.Windows.Input.KeyboardEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using ProgressBar = System.Windows.Controls.ProgressBar;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using TextDataFormat = System.Windows.TextDataFormat;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Password Vault - UI only. The encrypted file, entries, TOTP, health, CSV / backup and settings are in
    /// <see cref="PasswordVaultManager"/>. While the vault is unlocked it can lock itself (idle, minimize, hidden to the
    /// tray, Windows session lock); locking wipes the key and empties every field that could hold a secret.
    /// Copied secrets stay out of clipboard history and are cleared after a delay, only if still ours.
    /// </summary>
    public partial class PasswordVaultView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private static string DialogTitle => Loc.T("Str_Tool_password-vault_Name", "Password Vault");

        private readonly PasswordVaultManager _vault = new();
        private readonly ObservableCollection<object> _shown = new();      // VaultGroupRow headers and VaultEntry rows
        private VaultHealthKind? _healthFilter;                             // footer chip that is filtering the list, if any
        private readonly HashSet<string> _collapsed = new();               // group path keys; session only, never saved
        private readonly DispatcherTimer _tick;
        private readonly DispatcherTimer _clipboardTimer;
        private readonly DispatcherTimer _undoTimer;
        private VaultSettings _settings;
        private readonly bool _viewConstructed;

        private PasswordVaultWidgetWindow? _widget;
        private Window? _host;
        private string? _clipboardSecret;
        private VaultEntry? _editing;          // a copy of the entry in the editor (never the manager's own instance)
        private VaultEntry? _undoEntry;
        private bool _isNew, _dirty, _loadingEditor, _passwordShown, _suppressSelection, _readOnly, _loadedOnce, _inputHooked;
        private string? _totpCode;
        private DateTime _lastActivity = DateTime.UtcNow;

        public PasswordVaultView()
        {
            InitializeComponent();
            ListEntries.ItemsSource = _shown;
            _settings = PasswordVaultManager.LoadSettings();

            ApplySettingsToControls();

            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (_, _) => OnTick();
            _clipboardTimer = new DispatcherTimer();
            _clipboardTimer.Tick += (_, _) =>
            {
                _clipboardTimer.Stop();
                if (ClearClipboardIfOurs()) TxtEditorStatus.Text = T("Str_Vault_ClipboardCleared", "Clipboard cleared.");
            };
            _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _undoTimer.Tick += (_, _) => DropUndo();

            _vault.Unlocked += OnUnlocked;
            _vault.Locked += OnLocked;
            _vault.EntriesChanged += OnEntriesChanged;

            PasswordVaultManager.SettingsSaved += OnSettingsSaved;

            _viewConstructed = true;
            ShortcutManager.Attach("vault.lock", LockVault);
        }

        /// <summary>The Settings page (or another save) changed the settings file: take its values.</summary>
        private void OnSettingsSaved()
        {
            _settings = PasswordVaultManager.LoadSettings();
            ApplySettingsToControls();
            UpdateAutoLockBadge();
        }

        /// <summary>"Locks in m:ss" counting down from the last activity; amber for the last 30 seconds.</summary>
        private void UpdateAutoLockBadge()
        {
            if (_settings.AutoLockMinutes <= 0)
            {
                TxtAutoLock.Text = T("Str_Vault_AutoLockOff", "Auto-lock off");
                SetAutoLockBadgeWarning(false);
                return;
            }
            var left = TimeSpan.FromMinutes(_settings.AutoLockMinutes) - (DateTime.UtcNow - _lastActivity);
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            TxtAutoLock.Text = string.Format(T("Str_Vault_LocksIn", "Locks in {0}"), $"{(int)left.TotalMinutes}:{left.Seconds:00}");
            SetAutoLockBadgeWarning(left.TotalSeconds < 30);
        }

        private void SetAutoLockBadgeWarning(bool warn)
        {
            BadgeAutoLock.SetResourceReference(Border.BackgroundProperty, warn ? "StatusWarningBg" : "StatusInfoBg");
            string fg = warn ? "StatusWarningFg" : "StatusInfoFg";
            IconAutoLock.SetResourceReference(PackIcon.ForegroundProperty, fg);
            TxtAutoLock.SetResourceReference(TextBlock.ForegroundProperty, fg);
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _host = Window.GetWindow(this);
            if (_host != null)
            {
                _host.StateChanged += (_, _) =>
                {
                    if (_host.WindowState == WindowState.Minimized && _settings.LockOnMinimize) LockVault();
                };
                _host.IsVisibleChanged += (_, args) =>
                {
                    if (args.NewValue is false && _settings.LockOnMinimize) LockVault();   // hidden to the tray
                };
            }
            UpdateState();
            if (_settings.WidgetEnabled) ShowWidget();
        }

        /// <summary>Called when the app closes (and by the shell): lock, wipe, close the widget, clear our clipboard text.</summary>
        public void CancelActiveRun()
        {
            PasswordVaultManager.SettingsSaved -= OnSettingsSaved;
            _tick.Stop();
            _clipboardTimer.Stop();
            _undoTimer.Stop();
            ClearClipboardIfOurs();
            CloseWidgetWindow();
            try { _vault.Dispose(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        }

        // =================================================================
        //  State (create / locked / unlocked) and locking
        // =================================================================

        private void UpdateState()
        {
            bool unlocked = _vault.IsUnlocked, exists = _vault.VaultExists;
            PanelVault.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
            PanelLocked.Visibility = !unlocked && exists ? Visibility.Visible : Visibility.Collapsed;
            PanelSetup.Visibility = !unlocked && !exists ? Visibility.Visible : Visibility.Collapsed;
            SidebarHost.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
            OptionsColumn.Width = new GridLength(unlocked ? SidebarExpandedWidth : 0);
            GapColumn.Width = new GridLength(unlocked ? 14 : 0);

            FrameworkElement active = unlocked ? PanelVault : exists ? PanelLocked : PanelSetup;
            UiMotion.FadeIn(active);
            Dispatcher.BeginInvoke(() =>
            {
                if (unlocked) TxtSearch.Focus();
                else if (exists) PwdUnlock.Focus();
                else PwdNew.Focus();
            }, DispatcherPriority.Input);
        }

        private void OnUnlocked()
        {
            _lastActivity = DateTime.UtcNow;
            UpdateAutoLockBadge();
            HookActivity(true);
            _tick.Start();
            _widget?.SetLocked(false);
            RefreshList();
            RefreshWidget();
            UpdateState();
        }

        private void OnLocked()
        {
            HookActivity(false);
            _tick.Stop();
            ClearClipboardIfOurs();
            DropUndo();
            ClearEditor();
            _shown.Clear();
            _collapsed.Clear();
            _healthFilter = null;
            TxtSearch.Clear();
            PwdUnlock.Clear();
            TxtUnlockError.Text = "";
            BtnRestoreFile.Visibility = Visibility.Collapsed;
            _widget?.SetLocked(true);
            UpdateState();
        }

        private void OnEntriesChanged()
        {
            if (!_vault.IsUnlocked) return;
            RefreshList();
            RefreshWidget();
        }

        private void LockVault()
        {
            if (!_vault.IsUnlocked) return;
            _vault.Lock();
        }

        private void HookActivity(bool on)
        {
            if (on == _inputHooked) return;
            _inputHooked = on;
            if (on)
            {
                InputManager.Current.PreProcessInput += OnPreProcessInput;
                SystemEvents.SessionSwitch += OnSessionSwitch;
            }
            else
            {
                InputManager.Current.PreProcessInput -= OnPreProcessInput;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
            }
        }

        private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is MouseEventArgs or KeyboardEventArgs) _lastActivity = DateTime.UtcNow;
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (!_settings.LockOnSessionLock) return;
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                Dispatcher.BeginInvoke(LockVault);
        }

        private void OnTick()
        {
            if (!_vault.IsUnlocked) return;
            if (_settings.AutoLockMinutes > 0 && DateTime.UtcNow - _lastActivity >= TimeSpan.FromMinutes(_settings.AutoLockMinutes))
            {
                LockVault();
                return;
            }
            UpdateAutoLockBadge();
            UpdateTotp();
        }

        private void BtnLock_Click(object sender, RoutedEventArgs e) => LockVault();

        // =================================================================
        //  Create the vault
        // =================================================================

        private void Pwd_KeyUp(object sender, KeyEventArgs e)
        {
            CapsSetup.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;
            if (e.Key == Key.Enter && sender == PwdConfirm) BtnCreate_Click(sender, e);
        }

        private void PwdNew_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            char[] pw = VaultPasswordDialog.ReadSecure(PwdNew);
            try
            {
                double bits = PasswordVaultManager.EstimateBits(pw);
                var grade = PasswordGeneratorManager.Grade(bits);
                ShowStrength(SetupMeter, TxtSetupStrength, pw.Length == 0 ? null : grade, bits);
            }
            finally
            {
                VaultPasswordDialog.Wipe(pw);
            }
            UpdateMatch();
        }

        private void PwdConfirm_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_viewConstructed) UpdateMatch();
        }

        private void UpdateMatch()
        {
            char[] a = VaultPasswordDialog.ReadSecure(PwdNew), b = VaultPasswordDialog.ReadSecure(PwdConfirm);
            try
            {
                if (b.Length == 0) { TxtMatch.Text = ""; return; }
                bool same = a.AsSpan().SequenceEqual(b);
                TxtMatch.Text = same ? T("Str_Vault_Match", "Passwords match") : T("Str_Vault_NoMatch", "The passwords don't match.");
                TxtMatch.SetResourceReference(TextBlock.ForegroundProperty, same ? "StatusSuccessFg" : "StatusErrorFg");
            }
            finally
            {
                VaultPasswordDialog.Wipe(a);
                VaultPasswordDialog.Wipe(b);
            }
        }

        private async void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            char[] a = VaultPasswordDialog.ReadSecure(PwdNew), b = VaultPasswordDialog.ReadSecure(PwdConfirm);
            try
            {
                TxtSetupError.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                if (!a.AsSpan().SequenceEqual(b)) { TxtSetupError.Text = T("Str_Vault_NoMatch", "The passwords don't match."); return; }
                if (a.Length < 10) { TxtSetupError.Text = T("Str_Vault_TooShort", "Use at least 10 characters."); return; }
                if (PasswordVaultManager.EstimateBits(a) < PasswordVaultManager.WeakBelowBits &&
                    ThemedMessageBox.Show(T("Str_Vault_WeakWarn", "This password is weak and could be guessed. Use it anyway?"), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                BtnCreate.IsEnabled = false;
                TxtSetupError.Text = T("Str_Vault_Working", "Working...");
                TxtSetupError.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                try
                {
                    await _vault.CreateAsync(a);
                    PwdNew.Clear();
                    PwdConfirm.Clear();
                    TxtSetupError.Text = "";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException or InvalidOperationException)
                {
                    TxtSetupError.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                    TxtSetupError.Text = PasswordVaultManager.PlainError(ex);
                }
            }
            finally
            {
                VaultPasswordDialog.Wipe(a);
                VaultPasswordDialog.Wipe(b);
                BtnCreate.IsEnabled = true;
            }
        }

        // =================================================================
        //  Unlock
        // =================================================================

        private void PwdUnlock_KeyUp(object sender, KeyEventArgs e)
        {
            CapsUnlock.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;
            if (e.Key == Key.Enter) BtnUnlock_Click(sender, e);
        }

        private async void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnUnlock.IsEnabled) return;
            char[] pw = VaultPasswordDialog.ReadSecure(PwdUnlock);
            BtnUnlock.IsEnabled = false;
            TxtUnlockError.Text = T("Str_Vault_Unlocking", "Unlocking...");
            TxtUnlockError.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            try
            {
                var result = await _vault.UnlockAsync(pw);
                TxtUnlockError.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                switch (result)
                {
                    case VaultOpenResult.Ok:
                        TxtUnlockError.Text = "";
                        PwdUnlock.Clear();
                        break;
                    case VaultOpenResult.WrongPasswordOrTampered:
                        TxtUnlockError.Text = T("Str_Vault_WrongPassword", "Wrong password, or the vault file was changed.");
                        PwdUnlock.Clear();
                        PwdUnlock.Focus();
                        break;
                    case VaultOpenResult.NotAVault:
                        TxtUnlockError.Text = T("Str_Vault_NotAVault", "The vault file can't be read.");
                        BtnRestoreFile.Visibility = _vault.HasBackupFile ? Visibility.Visible : Visibility.Collapsed;
                        break;
                    default:
                        UpdateState();
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                TxtUnlockError.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                TxtUnlockError.Text = PasswordVaultManager.PlainError(ex);
            }
            finally
            {
                VaultPasswordDialog.Wipe(pw);
                BtnUnlock.IsEnabled = true;
            }
        }

        private void BtnRestoreFile_Click(object sender, RoutedEventArgs e)
        {
            if (ThemedMessageBox.Show(T("Str_Vault_RestoreConfirm", "Put the last good copy of the vault back? The unreadable file is kept next to it, not deleted."), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            bool ok = _vault.RestoreFromBackupFile();
            TxtUnlockError.SetResourceReference(TextBlock.ForegroundProperty, ok ? "StatusSuccessFg" : "StatusErrorFg");
            TxtUnlockError.Text = ok ? T("Str_Vault_Restored", "Restored. Enter your password to unlock.") : T("Str_Vault_RestoreFailed", "Couldn't restore the copy.");
            BtnRestoreFile.Visibility = Visibility.Collapsed;
        }

        // =================================================================
        //  List, search, health
        // =================================================================

        private void RefreshList()
        {
            Guid? keep = _editing?.Id;
            var issues = _vault.Health();
            var byEntry = issues.GroupBy(i => i.EntryId).ToDictionary(g => g.Key, g => g.Select(i => i.Kind).ToList());
            foreach (var entry in _vault.Entries)
            {
                if (byEntry.TryGetValue(entry.Id, out var kinds))
                {
                    entry.HealthNote = string.Join(", ", kinds.Select(KindText));
                    entry.HealthSeverity = kinds.Contains(VaultHealthKind.Weak) ? 3 : kinds.Contains(VaultHealthKind.Reused) ? 2 : 1;
                }
                else
                {
                    entry.HealthNote = "";
                    entry.HealthSeverity = 0;
                }
            }

            int weak = issues.Count(i => i.Kind == VaultHealthKind.Weak), reused = issues.Count(i => i.Kind == VaultHealthKind.Reused), old = issues.Count(i => i.Kind == VaultHealthKind.Old);
            UpdateHealthFooter(weak, reused, old);

            IEnumerable<VaultEntry> found = _vault.Search(TxtSearch.Text);
            if (_healthFilter is VaultHealthKind filterKind)
                found = found.Where(en => byEntry.TryGetValue(en.Id, out var k) && k.Contains(filterKind));

            // The entry being edited must stay visible after a save, so open the groups it sits in.
            if (_editing != null)
            {
                var path = PasswordVaultManager.GroupPath(_editing.Tags);
                if (path.Length == 0) _collapsed.Remove(PasswordVaultManager.UngroupedKey);
                for (int i = 1; i <= path.Length; i++) _collapsed.Remove(string.Join("/", path.Take(i)).ToLowerInvariant());
            }

            bool expandAll = TxtSearch.Text.Trim().Length > 0 || _healthFilter != null;
            var list = PasswordVaultManager.BuildRows(found, _collapsed, expandAll, T("Str_Vault_Ungrouped", "Ungrouped"));

            _suppressSelection = true;
            try
            {
                _shown.Clear();
                foreach (var row in list) _shown.Add(row);
                ListEntries.SelectedItem = keep == null ? null : list.OfType<VaultEntry>().FirstOrDefault(en => en.Id == keep);
            }
            finally
            {
                _suppressSelection = false;
            }

            PanelListEmpty.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool filtered = _vault.Entries.Count > 0;
            TxtEmptyTitle.Text = filtered ? T("Str_Vault_NoMatches", "Nothing matches") : T("Str_Vault_EmptyTitle", "No passwords yet");
            TxtEmptyHint.Text = filtered ? T("Str_Vault_NoMatchesHint", "Try another search, or clear the problems filter.") : T("Str_Vault_EmptyHint", "Add your first login, or import a CSV from your browser.");
        }

        private static string KindText(VaultHealthKind k) => k switch
        {
            VaultHealthKind.Weak => T("Str_Vault_NoteWeak", "Weak password"),
            VaultHealthKind.Reused => T("Str_Vault_NoteReused", "Used on more than one entry"),
            _ => T("Str_Vault_NoteOld", "Not changed in over a year"),
        };

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewConstructed && _vault.IsUnlocked) RefreshList();
        }

        /// <summary>Footer: a colored chip with a count for each problem type that exists (weak red, reused amber, old blue), or "No problems found".</summary>
        private void UpdateHealthFooter(int weak, int reused, int old)
        {
            // A filter whose problems were all fixed has nothing left to show.
            if (_healthFilter == VaultHealthKind.Weak && weak == 0 || _healthFilter == VaultHealthKind.Reused && reused == 0 || _healthFilter == VaultHealthKind.Old && old == 0)
                _healthFilter = null;

            FooterHealth.Visibility = _vault.Entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            SetChip(ChipWeak, TxtChipWeak, weak, "Str_Vault_HealthWeak", "weak", VaultHealthKind.Weak);
            SetChip(ChipReused, TxtChipReused, reused, "Str_Vault_HealthReused", "reused", VaultHealthKind.Reused);
            SetChip(ChipOld, TxtChipOld, old, "Str_Vault_HealthOldShort", "old", VaultHealthKind.Old);
            HealthAllGood.Visibility = weak + reused + old == 0 ? Visibility.Visible : Visibility.Collapsed;

            void SetChip(ToggleButton chip, TextBlock label, int count, string key, string fallback, VaultHealthKind kind)
            {
                chip.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                chip.IsChecked = _healthFilter == kind;
                label.Text = $"{count} {T(key, fallback)}";
            }
        }

        private void HealthChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton { Tag: string name } || !Enum.TryParse(name, out VaultHealthKind kind)) return;
            _healthFilter = _healthFilter == kind ? null : kind;
            RefreshList();
        }

        /// <summary>A group header was clicked: fold or unfold that group.</summary>
        private void BtnGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: VaultGroupRow group }) return;
            if (!_collapsed.Remove(group.PathKey)) _collapsed.Add(group.PathKey);
            RefreshList();
        }

        private void ListEntries_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _suppressSelection) return;
            var next = ListEntries.SelectedItem as VaultEntry;
            if (next != null && _editing != null && next.Id == _editing.Id && !_isNew) return;
            if (!ConfirmDiscard())
            {
                _suppressSelection = true;
                ListEntries.SelectedItem = _editing == null ? null : _shown.OfType<VaultEntry>().FirstOrDefault(en => en.Id == _editing.Id);
                _suppressSelection = false;
                return;
            }
            if (next == null) ClearEditor();
            else LoadEditor(next.Clone(), isNew: false);
        }

        private bool ConfirmDiscard() =>
            !_dirty || ThemedMessageBox.Show(T("Str_Vault_DiscardChanges", "Discard the changes you haven't saved?"), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // =================================================================
        //  Editor
        // =================================================================

        private void LoadEditor(VaultEntry entry, bool isNew)
        {
            _loadingEditor = true;
            try
            {
                _editing = entry;
                _isNew = isNew;
                TxtTitle.Text = entry.Title;
                TxtUsername.Text = entry.Username;
                TxtUrl.Text = entry.Url;
                TxtTags.Text = entry.Tags;
                TxtNotes.Text = entry.Notes;
                TxtTotpSecret.Text = entry.TotpSecret;
                BtnFavorite.IsChecked = entry.Favorite;
                SetFavoriteIcon();
                SetPasswordShown(false);
                PwdEntry.Password = entry.Password;
                TxtEntryPlain.Text = "";
                BtnDelete.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
                SetReadOnly(!isNew);
                TxtEditorStatus.Text = "";
                _dirty = false;
            }
            finally
            {
                _loadingEditor = false;
            }
            UpdateEntryStrength();
            UpdateTotp();
            UpdateEditorVisibility();
            UiMotion.FadeIn(PanelEditor);
        }

        /// <summary>The editor card only takes room while an entry is open; otherwise the list gets the full width.</summary>
        private void UpdateEditorVisibility()
        {
            bool open = _editing != null;
            EditorGapColumn.Width = new GridLength(open ? 14 : 0);
            EditorColumn.Width = open ? new GridLength(1.35, GridUnitType.Star) : new GridLength(0);
            EditorColumn.MinWidth = open ? 320 : 0;
            CardEditor.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>An opened entry is shown read-only (copy and reveal still work) until Edit is pressed; a new entry starts editable.</summary>
        private void SetReadOnly(bool readOnly)
        {
            _readOnly = readOnly;
            foreach (var box in new[] { TxtTitle, TxtUsername, TxtUrl, TxtTags, TxtNotes, TxtTotpSecret, TxtEntryPlain })
                box.IsReadOnly = readOnly;
            PwdEntry.IsHitTestVisible = !readOnly;
            PwdEntry.Focusable = !readOnly;
            BtnFavorite.IsEnabled = !readOnly;
            BtnGenerate.IsEnabled = !readOnly;
            BtnEdit.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
            BtnSave.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
            HdrEditor.Content = readOnly ? T("Str_Vault_ViewEntry", "Entry details")
                : _isNew ? T("Str_Vault_NewEntryTitle", "New entry") : T("Str_Vault_EditEntry", "Edit entry");
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            SetReadOnly(false);
            TxtEditorStatus.Text = "";
            TxtTitle.Focus();
            TxtTitle.CaretIndex = TxtTitle.Text.Length;
        }

        // ---- Right-click menu on a list row ----

        private static VaultEntry? MenuEntry(object sender) => (sender as FrameworkElement)?.DataContext as VaultEntry;

        /// <summary>Right-click selects the row first (like Explorer), so the editor shows the entry the menu is about.</summary>
        private void EntryRow_RightClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is VaultEntry entry && !ReferenceEquals(ListEntries.SelectedItem, entry))
                ListEntries.SelectedItem = entry;
        }

        private void MenuEdit_Click(object sender, RoutedEventArgs e)
        {
            var entry = MenuEntry(sender);
            if (entry == null) return;
            if (_editing?.Id != entry.Id) ListEntries.SelectedItem = entry;   // asks before discarding unsaved edits
            if (_editing?.Id == entry.Id) BtnEdit_Click(sender, e);
        }

        private void MenuCopyUser_Click(object sender, RoutedEventArgs e) => CopyWithStatus(MenuEntry(sender)?.Username);

        private void MenuCopyPass_Click(object sender, RoutedEventArgs e) => CopyWithStatus(MenuEntry(sender)?.Password);

        /// <summary>Right-click > Move to group: changes the entry's group (its first tag) and saves it.</summary>
        private void MenuMoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (MenuEntry(sender) is not { } entry) return;
            if (_editing?.Id == entry.Id && !ConfirmDiscard()) return;

            string current = string.Join("/", PasswordVaultManager.GroupPath(entry.Tags));
            var dlg = new VaultGroupDialog(
                DialogTitle,
                T("Str_Vault_MoveToGroupHint", "Pick a group or type a new one (Games/Launchers). Leave empty for no group."),
                PasswordVaultManager.ExistingGroups(_vault.Entries),
                current);
            dlg.ShowDialog();
            if (dlg.Result == null) return;

            string tags = PasswordVaultManager.WithGroup(entry.Tags, dlg.Result);
            if (tags == (entry.Tags ?? "").Trim()) return;
            var moved = entry.Clone();
            moved.Tags = tags;
            try { _vault.Upsert(moved); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ThemedMessageBox.Show(PasswordVaultManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_editing?.Id == entry.Id) { _editing = moved.Clone(); _loadingEditor = true; TxtTags.Text = moved.Tags; _loadingEditor = false; _dirty = false; }
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (MenuEntry(sender) is { } entry) DeleteEntry(entry.Id);
        }

        private void BtnCloseEditor_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscard()) return;
            _suppressSelection = true;
            ListEntries.SelectedItem = null;
            _suppressSelection = false;
            ClearEditor();
        }

        /// <summary>Empties every field that could hold a secret.</summary>
        private void ClearEditor()
        {
            _loadingEditor = true;
            try
            {
                _editing = null;
                _isNew = false;
                _dirty = false;
                _totpCode = null;
                TxtTitle.Clear();
                TxtUsername.Clear();
                TxtUrl.Clear();
                TxtTags.Clear();
                TxtNotes.Clear();
                TxtTotpSecret.Clear();
                TxtEntryPlain.Clear();
                PwdEntry.Clear();
                TxtTotpCode.Text = "";
                SetPasswordShown(false);
                TotpBox.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _loadingEditor = false;
            }
            UpdateEditorVisibility();
        }

        private string CurrentPassword => _passwordShown ? TxtEntryPlain.Text : PwdEntry.Password;

        private void SetPasswordShown(bool show)
        {
            bool wasLoading = _loadingEditor;
            _loadingEditor = true;
            try
            {
                if (show == _passwordShown) return;
                if (show)
                {
                    TxtEntryPlain.Text = PwdEntry.Password;
                    PwdEntry.Clear();
                }
                else
                {
                    PwdEntry.Password = TxtEntryPlain.Text;
                    TxtEntryPlain.Clear();
                }
                _passwordShown = show;
                PwdEntry.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
                TxtEntryPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                IconReveal.Kind = show ? PackIconKind.EyeOffOutline : PackIconKind.EyeOutline;
            }
            finally
            {
                _loadingEditor = wasLoading;
            }
        }

        private void BtnReveal_Click(object sender, RoutedEventArgs e) => SetPasswordShown(!_passwordShown);

        private void PwdEntry_PasswordChanged(object sender, RoutedEventArgs e) => OnPasswordEdited();

        private void TxtEntryPlain_TextChanged(object sender, TextChangedEventArgs e) => OnPasswordEdited();

        private void OnPasswordEdited()
        {
            if (!_viewConstructed || _loadingEditor) return;
            _dirty = true;
            UpdateEntryStrength();
        }

        private void Editor_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _loadingEditor) return;
            _dirty = true;
            TxtEditorStatus.Text = "";
            if (sender == BtnFavorite) SetFavoriteIcon();
        }

        private void SetFavoriteIcon()
        {
            if (BtnFavorite.Content is PackIcon icon) icon.Kind = BtnFavorite.IsChecked == true ? PackIconKind.Star : PackIconKind.StarOutline;
        }

        private void UpdateEntryStrength()
        {
            string pw = CurrentPassword;
            if (pw.Length == 0) { ShowStrength(EntryMeter, TxtEntryStrength, null, 0); return; }
            double bits = PasswordVaultManager.EstimateBits(pw);
            ShowStrength(EntryMeter, TxtEntryStrength, PasswordGeneratorManager.Grade(bits), bits);
        }

        private static void ShowStrength(ProgressBar bar, TextBlock label, PasswordGenStrength? grade, double bits)
        {
            if (grade == null)
            {
                bar.Value = 0;
                label.Text = "";
                return;
            }
            bar.Value = Math.Min(100, bits);
            bar.SetResourceReference(ProgressBar.ForegroundProperty, grade.Value switch
            {
                PasswordGenStrength.VeryWeak or PasswordGenStrength.Weak => "StatusErrorFg",
                PasswordGenStrength.Fair => "StatusWarningFg",
                PasswordGenStrength.Strong => "StatusInfoFg",
                _ => "StatusSuccessFg",
            });
            label.Text = $"{PasswordGeneratorManager.StrengthLabel(grade.Value)} · {bits:0} bits";
        }

        private void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            string generated = PasswordGeneratorManager.Generate(new PasswordGenOptions { Length = 20 });
            SetPasswordShown(true);
            TxtEntryPlain.Text = generated;   // fires OnPasswordEdited -> dirty + meter
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscard()) return;
            _suppressSelection = true;
            ListEntries.SelectedItem = null;
            _suppressSelection = false;
            LoadEditor(_vault.NewEntry(), isNew: true);
            TxtTitle.Focus();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            TxtEditorStatus.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
            if (string.IsNullOrWhiteSpace(TxtTitle.Text)) { TxtEditorStatus.Text = T("Str_Vault_TitleRequired", "Give it a title."); TxtTitle.Focus(); return; }
            if (TxtTotpSecret.Text.Trim().Length > 0 && PasswordVaultManager.ParseTotp(TxtTotpSecret.Text) == null)
            {
                TxtEditorStatus.Text = T("Str_Vault_TotpInvalid", "That one-time code secret isn't valid.");
                TxtTotpSecret.Focus();
                return;
            }

            var toSave = _editing.Clone();
            toSave.Title = TxtTitle.Text.Trim();
            toSave.Username = TxtUsername.Text.Trim();
            toSave.Password = CurrentPassword;
            toSave.Url = TxtUrl.Text.Trim();
            toSave.Tags = TxtTags.Text.Trim();
            toSave.Notes = TxtNotes.Text;
            toSave.TotpSecret = TxtTotpSecret.Text.Trim();
            toSave.Favorite = BtnFavorite.IsChecked == true;
            try
            {
                _editing = toSave;                 // so the list re-selects it after the refresh
                _vault.Upsert(toSave);
                _editing = toSave.Clone();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                TxtEditorStatus.Text = PasswordVaultManager.PlainError(ex);
                return;
            }
            _isNew = false;
            _dirty = false;
            BtnDelete.Visibility = Visibility.Visible;
            SetReadOnly(true);
            TxtEditorStatus.SetResourceReference(TextBlock.ForegroundProperty, "StatusSuccessFg");
            TxtEditorStatus.Text = T("Str_Vault_Saved", "Saved");
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            if (_isNew)
            {
                ClearEditor();
                return;
            }
            DeleteEntry(_editing.Id);
        }

        /// <summary>Removes an entry (from the editor or the right-click menu) and offers Undo for a few seconds.</summary>
        private void DeleteEntry(Guid id)
        {
            var victim = _vault.Entries.FirstOrDefault(en => en.Id == id);
            if (_editing?.Id == id)
            {
                _suppressSelection = true;
                ListEntries.SelectedItem = null;
                _suppressSelection = false;
                ClearEditor();
            }
            if (victim == null) return;
            DropUndo();
            _undoEntry = victim.Clone();
            try { _vault.Remove(victim.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ThemedMessageBox.Show(PasswordVaultManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                _undoEntry = null;
                return;
            }
            UndoBar.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            var back = _undoEntry;
            _undoEntry = null;
            _undoTimer.Stop();
            UndoBar.Visibility = Visibility.Collapsed;
            if (back == null || !_vault.IsUnlocked) return;
            try { _vault.Upsert(back); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ThemedMessageBox.Show(PasswordVaultManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>The undo window ended (or the vault locked): forget the deleted entry.</summary>
        private void DropUndo()
        {
            _undoTimer.Stop();
            UndoBar.Visibility = Visibility.Collapsed;
            if (_undoEntry != null)
            {
                _undoEntry.Password = "";
                _undoEntry.TotpSecret = "";
                _undoEntry.Notes = "";
                _undoEntry = null;
            }
        }

        private void BtnOpenUrl_Click(object sender, RoutedEventArgs e)
        {
            string text = TxtUrl.Text.Trim();
            if (text.Length == 0) return;
            if (!text.Contains("://")) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                TxtEditorStatus.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorFg");
                TxtEditorStatus.Text = T("Str_Vault_UrlInvalid", "Only web addresses (http / https) can be opened.");
                return;
            }
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
        }

        // =================================================================
        //  One-time codes
        // =================================================================

        private void TxtTotpSecret_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || _loadingEditor) return;
            _dirty = true;
            UpdateTotp();
        }

        private void UpdateTotp()
        {
            if (_editing == null) { TotpBox.Visibility = Visibility.Collapsed; return; }
            string? code = PasswordVaultManager.TotpCode(TxtTotpSecret.Text, DateTimeOffset.UtcNow, out int left);
            _totpCode = code;
            if (code == null) { TotpBox.Visibility = Visibility.Collapsed; return; }
            TotpBox.Visibility = Visibility.Visible;
            TxtTotpCode.Text = code.Length > 4 ? code[..(code.Length / 2)] + " " + code[(code.Length / 2)..] : code;
            TxtTotpLeft.Text = string.Format(T("Str_Vault_Seconds", "{0} s"), left);
        }

        private void BtnCopyTotp_Click(object sender, RoutedEventArgs e) => CopyWithStatus(_totpCode);

        // =================================================================
        //  Clipboard (kept out of history, cleared after the chosen delay)
        // =================================================================

        private void BtnCopyUser_Click(object sender, RoutedEventArgs e) => CopyWithStatus(TxtUsername.Text);

        private void BtnCopyPass_Click(object sender, RoutedEventArgs e) => CopyWithStatus(CurrentPassword);

        private void CopyWithStatus(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool ok = CopySecret(text);
            TxtEditorStatus.SetResourceReference(TextBlock.ForegroundProperty, ok ? "TextSecondary" : "StatusErrorFg");
            TxtEditorStatus.Text = ok
                ? string.Format(T("Str_Vault_Copied", "Copied. Clipboard clears in {0} s."), _settings.ClipboardSeconds)
                : T("Str_Vault_CopyFailed", "Couldn't copy - another program is using the clipboard.");
        }

        /// <summary>Puts the text on the clipboard marked so Windows keeps it out of history (Win+V) and cloud sync, and clipboard managers skip it.</summary>
        private bool CopySecret(string text)
        {
            try
            {
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
                Clipboard.SetDataObject(data, true);
                _clipboardSecret = text;
                _clipboardTimer.Stop();
                _clipboardTimer.Interval = TimeSpan.FromSeconds(_settings.ClipboardSeconds);
                _clipboardTimer.Start();
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        /// <summary>Clears the clipboard only if it still holds what we copied.</summary>
        private bool ClearClipboardIfOurs()
        {
            if (_clipboardSecret == null) return false;
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == _clipboardSecret)
                {
                    Clipboard.Clear();
                    return true;
                }
                return false;
            }
            catch (ExternalException)
            {
                return false;
            }
            finally
            {
                _clipboardSecret = null;
            }
        }

        // =================================================================
        //  Settings
        // =================================================================

        private void ApplySettingsToControls()
        {
            BtnWidget.Content = T(_settings.WidgetEnabled ? "Str_Vault_HideWidget" : "Str_Vault_ShowWidget", _settings.WidgetEnabled ? "Hide quick-access widget" : "Show quick-access widget");
        }

        // =================================================================
        //  Master password, import / export, backups
        // =================================================================

        private async void BtnChangeMaster_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VaultPasswordDialog(T("Str_Vault_ChangeMaster", "Change master password"), T("Str_Vault_ChangeMasterHint", "Your vault is re-encrypted with a new key. Use something you can remember and nobody can guess."),
                new[] { T("Str_Vault_DlgCurrent", "Current master password"), T("Str_Vault_DlgNew", "New master password"), T("Str_Vault_ConfirmPassword", "Confirm password") }, lastTwoMustMatch: true);
            dialog.ShowDialog();
            var values = dialog.Result;
            if (values == null) return;
            try
            {
                bool ok = await _vault.ChangeMasterPasswordAsync(values[0], values[1]);
                ThemedMessageBox.Show(ok ? T("Str_Vault_ChangedOk", "Master password changed.") : T("Str_Vault_WrongCurrent", "The current master password is wrong."), DialogTitle, MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                ThemedMessageBox.Show(PasswordVaultManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                foreach (var v in values) VaultPasswordDialog.Wipe(v);
            }
        }

        private void BtnImportCsv_Click(object sender, RoutedEventArgs e)
        {
            var open = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*", Title = T("Str_Vault_ImportCsv", "Import CSV") };
            if (open.ShowDialog() != true) return;
            try
            {
                int added = _vault.ImportCsv(open.FileName);
                ThemedMessageBox.Show(string.Format(T("Str_Vault_ImportDone", "Added {0} entries."), added) + "\n\n" + T("Str_Vault_DeleteCsvHint", "The CSV file is not encrypted - delete it now that it's imported."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ThemedMessageBox.Show(PasswordVaultManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (ThemedMessageBox.Show(T("Str_Vault_CsvWarning", "The CSV file is NOT encrypted. Anyone who gets it can read every password. Delete it when you are done. Export anyway?"), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var save = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "passwords.csv", Title = T("Str_Vault_ExportCsv", "Export CSV") };
            if (save.ShowDialog() != true) return;
            try { _vault.ExportCsv(save.FileName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ThemedMessageBox.Show(PasswordVaultManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnExportBackup_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VaultPasswordDialog(T("Str_Vault_ExportBackup", "Encrypted backup"), T("Str_Vault_BackupHint", "The backup has its own password. You need it to restore the backup."),
                new[] { T("Str_Vault_DlgBackupPassword", "Backup password"), T("Str_Vault_ConfirmPassword", "Confirm password") }, lastTwoMustMatch: true);
            dialog.ShowDialog();
            var values = dialog.Result;
            if (values == null) return;
            try
            {
                var save = new SaveFileDialog { Filter = "Smart Tools vault (*.svlt)|*.svlt", FileName = $"vault-backup-{DateTime.Now:yyyyMMdd}.svlt", Title = T("Str_Vault_ExportBackup", "Encrypted backup") };
                if (save.ShowDialog() != true) return;
                await _vault.ExportBackupAsync(save.FileName, values[0]);
                ThemedMessageBox.Show(T("Str_Vault_BackupSaved", "Backup saved."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                ThemedMessageBox.Show(PasswordVaultManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                foreach (var v in values) VaultPasswordDialog.Wipe(v);
            }
        }

        private async void BtnImportBackup_Click(object sender, RoutedEventArgs e)
        {
            var open = new OpenFileDialog { Filter = "Smart Tools vault (*.svlt)|*.svlt|All files (*.*)|*.*", Title = T("Str_Vault_ImportBackup", "Restore a backup") };
            if (open.ShowDialog() != true) return;
            var dialog = new VaultPasswordDialog(T("Str_Vault_ImportBackup", "Restore a backup"), T("Str_Vault_ImportBackupHint", "Entries in the backup that aren't in your vault yet are added."),
                new[] { T("Str_Vault_DlgBackupPassword", "Backup password") }, lastTwoMustMatch: false);
            dialog.ShowDialog();
            var values = dialog.Result;
            if (values == null) return;
            try
            {
                int? added = await _vault.ImportBackupAsync(open.FileName, values[0]);
                ThemedMessageBox.Show(added == null ? T("Str_Vault_BackupWrong", "Wrong password, or that isn't a vault backup.") : string.Format(T("Str_Vault_ImportDone", "Added {0} entries."), added),
                    DialogTitle, MessageBoxButton.OK, added == null ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                ThemedMessageBox.Show(PasswordVaultManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                foreach (var v in values) VaultPasswordDialog.Wipe(v);
            }
        }

        // =================================================================
        //  Floating widget
        // =================================================================

        private void BtnWidget_Click(object sender, RoutedEventArgs e)
        {
            if (_widget == null) ShowWidget();
            else HideWidget();
        }

        private void ShowWidget()
        {
            if (_widget != null) return;
            _widget = new PasswordVaultWidgetWindow(_settings);
            _widget.CloseRequested += HideWidget;
            _widget.PositionChanged += (left, top) =>
            {
                _settings.WidgetLeft = left;
                _settings.WidgetTop = top;
                PasswordVaultManager.SaveSettings(_settings);
            };
            _widget.LockRequested += LockVault;
            _widget.UnlockRequested += ShowMainWindow;
            _widget.SearchChanged += _ => RefreshWidget();
            _widget.CopyRequested += OnWidgetCopy;
            _widget.SetLocked(!_vault.IsUnlocked);
            _widget.Show();
            RefreshWidget();
            _settings.WidgetEnabled = true;
            PasswordVaultManager.SaveSettings(_settings);
            ApplySettingsToControls();
        }

        /// <summary>The user closed it: remember that, so it doesn't come back next time.</summary>
        private void HideWidget()
        {
            CloseWidgetWindow();
            _settings.WidgetEnabled = false;
            PasswordVaultManager.SaveSettings(_settings);
            ApplySettingsToControls();
        }

        /// <summary>Closes the window without touching the saved choice (used when the app exits).</summary>
        private void CloseWidgetWindow()
        {
            var w = _widget;
            _widget = null;
            w?.Close();
        }

        private void RefreshWidget()
        {
            if (_widget == null || !_vault.IsUnlocked) return;
            var matches = _vault.Search(_widget.SearchText).OrderByDescending(en => en.Favorite).ThenBy(en => en.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            _widget.SetEntries(matches, _vault.Entries.Count > 0);
        }

        private void OnWidgetCopy(Guid id, VaultWidgetCopy kind)
        {
            var entry = _vault.Entries.FirstOrDefault(en => en.Id == id);
            if (entry == null || _widget == null) return;
            string? text = kind switch
            {
                VaultWidgetCopy.Username => entry.Username,
                VaultWidgetCopy.Password => entry.Password,
                _ => PasswordVaultManager.TotpCode(entry.TotpSecret, DateTimeOffset.UtcNow, out _),
            };
            if (string.IsNullOrEmpty(text)) { _widget.ShowMessage(T("Str_Vault_NothingToCopy", "Nothing to copy.")); return; }
            _widget.ShowMessage(CopySecret(text)
                ? string.Format(T("Str_Vault_Copied", "Copied. Clipboard clears in {0} s."), _settings.ClipboardSeconds)
                : T("Str_Vault_CopyFailed", "Couldn't copy - another program is using the clipboard."));
        }

        private static void ShowMainWindow()
        {
            var w = Application.Current?.MainWindow;
            if (w == null) return;
            if (!w.IsVisible) w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        }
    }
}
