using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextDataFormat = System.Windows.TextDataFormat;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Password Generator - UI only. Generating, the exact strength, crack
    /// time, export and settings are in <see cref="PasswordGeneratorManager"/>.
    /// Passwords are never saved; a copied password is kept out of Windows'
    /// clipboard history and cleared from the clipboard after 30 seconds.
    /// </summary>
    public partial class PasswordGeneratorView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const string DialogTitle = "Password Generator";
        private static readonly TimeSpan ClipboardLifetime = TimeSpan.FromSeconds(30);

        private readonly BulkObservableCollection<PasswordGenItem> _results = new();
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _clipboardTimer;
        private readonly DispatcherTimer _copiedTimer;
        private readonly bool _viewConstructed;

        private string? _clipboardSecret;      // what we put on the clipboard, to clear it only if it's still ours
        private PasswordGenItem? _copiedItem;
        private bool _showAll;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _applyingSettings;

        public PasswordGeneratorView()
        {
            InitializeComponent();
            ListPasswords.ItemsSource = _results;

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettings();
            };
            _clipboardTimer = new DispatcherTimer { Interval = ClipboardLifetime };
            _clipboardTimer.Tick += (_, _) =>
            {
                _clipboardTimer.Stop();
                if (ClearClipboardIfOurs()) TxtResultsSubtitle.Text = "Clipboard cleared.";
            };
            _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            _copiedTimer.Tick += (_, _) =>
            {
                _copiedTimer.Stop();
                if (_copiedItem != null) _copiedItem.JustCopied = false;
                _copiedItem = null;
            };

            _viewConstructed = true;

            // F5 / Ctrl+G by default; the keys can be changed in Settings.
            ShortcutManager.Attach("password.generate", () => Generate(animate: true));
            ShortcutManager.Attach("password.generateAlt", () => Generate(animate: true));
        }

        /// <summary>Called from MainWindow.Window_Closing: clears a password we copied (if it's still on the clipboard) and saves the options.</summary>
        public void CancelActiveRun()
        {
            _clipboardTimer.Stop();
            _copiedTimer.Stop();
            ClearClipboardIfOurs();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SaveSettings();
            }
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;
        private Window? Owner => Window.GetWindow(this);
        private bool PassphraseMode => RadioModePassphrase.IsChecked == true;

        // =================================================================
        //  Layout
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            ApplySettings(PasswordGeneratorManager.LoadSettings());
            UpdatePanels(animate: false);
            UpdatePreview();
            Generate(animate: false);   // something useful on screen straight away
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Generate is a shortcut action (see the constructor).
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control
                     && ListPasswords.IsKeyboardFocusWithin && ListPasswords.SelectedItem is PasswordGenItem item)
            {
                e.Handled = true;
                CopyItem(item);
            }
        }

        // =================================================================
        //  Options
        // =================================================================

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            UpdatePanels(animate: true);
            UpdatePreview();
            QueueSave();
        }

        private void UpdatePanels(bool animate)
        {
            RandomOptionsPanel.Visibility = PassphraseMode ? Visibility.Collapsed : Visibility.Visible;
            PassphraseOptionsPanel.Visibility = PassphraseMode ? Visibility.Visible : Visibility.Collapsed;
            if (animate) UiMotion.FadeIn(PassphraseMode ? PassphraseOptionsPanel : RandomOptionsPanel, 4);
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton chip) UiMotion.Pop(chip, 0.9);
            UpdatePreview();
            QueueSave();
        }

        private void SliderLength_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtLengthValue != null) TxtLengthValue.Text = ((int)e.NewValue).ToString();
            if (Ignore) return;
            UpdatePreview();
            QueueSave();
        }

        private void SliderWordCount_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtWordCountValue != null) TxtWordCountValue.Text = ((int)e.NewValue).ToString();
            if (Ignore) return;
            UpdatePreview();
            QueueSave();
        }

        private void LengthPreset_Click(object sender, RoutedEventArgs e) => ApplyPreset(sender, SliderLength, TxtLengthValue);
        private void WordPreset_Click(object sender, RoutedEventArgs e) => ApplyPreset(sender, SliderWordCount, TxtWordCountValue);

        private static void ApplyPreset(object sender, Slider slider, FrameworkElement value)
        {
            if (sender is not Button { Tag: string tag } button || !int.TryParse(tag, out int n)) return;
            slider.Value = Math.Clamp(n, slider.Minimum, slider.Maximum);
            UiMotion.Pop(button, 0.9);
            UiMotion.Pop(value, 0.85);
        }

        private RadioButton[] SeparatorChips => new[] { RadioSepDash, RadioSepDot, RadioSepUnderscore, RadioSepSpace, RadioSepNone };
        private RadioButton[] BatchChips => new[] { RadioBatch1, RadioBatch5, RadioBatch10, RadioBatch20 };

        private static string SeparatorFor(string? tag) => tag switch
        {
            "dot" => ".",
            "underscore" => "_",
            "space" => " ",
            "none" => "",
            _ => "-",
        };

        private static string TagFor(string separator) => separator switch
        {
            "." => "dot",
            "_" => "underscore",
            " " => "space",
            "" => "none",
            _ => "dash",
        };

        private string SelectedSeparator => SeparatorFor(SeparatorChips.FirstOrDefault(c => c.IsChecked == true)?.Tag as string);

        private int SelectedBatch =>
            BatchChips.FirstOrDefault(c => c.IsChecked == true)?.Tag is string tag && int.TryParse(tag, out int n) ? n : 5;

        private PasswordGenOptions ReadOptions() => new()
        {
            Mode = PassphraseMode ? PasswordGenMode.Passphrase : PasswordGenMode.RandomCharacters,
            Length = (int)SliderLength.Value,
            IncludeUppercase = ChkUppercase.IsChecked == true,
            IncludeLowercase = ChkLowercase.IsChecked == true,
            IncludeNumbers = ChkNumbers.IsChecked == true,
            IncludeSymbols = ChkSymbols.IsChecked == true,
            ExcludeAmbiguous = ChkExcludeAmbiguous.IsChecked == true,
            RequireEveryType = ChkRequireEachCategory.IsChecked == true,
            WordCount = (int)SliderWordCount.Value,
            Separator = SelectedSeparator,
            CapitalizeWords = ChkCapitalizeWords.IsChecked == true,
            AppendNumber = ChkAppendNumber.IsChecked == true,
            BatchCount = SelectedBatch,
        };

        // =================================================================
        //  Live strength of the current settings
        // =================================================================

        private static (string Bg, string Fg, PackIconKind Icon) LookFor(PasswordGenStrength s) => s switch
        {
            PasswordGenStrength.VeryWeak or PasswordGenStrength.Weak => ("StatusErrorBg", "StatusErrorFg", PackIconKind.ShieldAlertOutline),
            PasswordGenStrength.Fair => ("StatusWarningBg", "StatusWarningFg", PackIconKind.ShieldHalfFull),
            _ => ("StatusSuccessBg", "StatusSuccessFg", PackIconKind.ShieldCheckOutline),
        };

        private void UpdatePreview()
        {
            var estimate = PasswordGeneratorManager.Estimate(ReadOptions());
            bool noTypes = !PassphraseMode && estimate.Error != null;
            TxtCharError.Visibility = noTypes ? Visibility.Visible : Visibility.Collapsed;
            BtnGenerate.IsEnabled = estimate.Error == null;

            string oldLabel = TxtPreviewLabel.Text;
            if (estimate.Error != null)
            {
                SetLook("StatusErrorBg", "StatusErrorFg", PackIconKind.ShieldOffOutline);
                TxtPreviewLabel.Text = "Can't generate";
                TxtPreviewDetail.Text = estimate.Error;
                TxtPreviewCrack.Text = "";
                TxtPreviewBits.Text = "—";
                PreviewMeter.Value = 0;
            }
            else
            {
                var (bg, fg, icon) = LookFor(estimate.Strength);
                SetLook(bg, fg, icon);
                TxtPreviewLabel.Text = estimate.Label;
                TxtPreviewDetail.Text = estimate.Detail;
                TxtPreviewCrack.Text = $"Cracked in {estimate.CrackTime} at 100 billion guesses a second.";
                TxtPreviewBits.Text = estimate.Bits.ToString("0");
                PreviewMeter.Value = estimate.Percent;
            }
            if (oldLabel != TxtPreviewLabel.Text) UiMotion.Pop(PreviewPill, 0.88);
        }

        private void SetLook(string bg, string fg, PackIconKind icon)
        {
            PreviewPill.SetResourceReference(Border.BackgroundProperty, bg);
            PreviewIcon.SetResourceReference(PackIcon.ForegroundProperty, fg);
            TxtPreviewLabel.SetResourceReference(TextBlock.ForegroundProperty, fg);
            PreviewMeter.SetResourceReference(ProgressBar.ForegroundProperty, fg);
            PreviewIcon.Kind = icon;
        }

        // =================================================================
        //  Generate
        // =================================================================

        private void BtnGenerate_Click(object sender, RoutedEventArgs e) => Generate(animate: true);

        private void Generate(bool animate)
        {
            var options = ReadOptions();
            List<PasswordGenItem> batch;
            try
            {
                batch = PasswordGeneratorManager.GenerateBatch(options);
            }
            catch (ArgumentException ex)
            {
                TxtResultsSubtitle.Text = ex.Message;
                UiMotion.Pop(PreviewPill, 0.88);
                return;
            }

            foreach (var item in batch) item.IsVisible = _showAll;
            _results.Clear();
            _results.AddRange(batch);
            ShowList(true);

            var estimate = batch[0].Estimate;
            TxtResultsSubtitle.Text = $"{batch.Count} {(batch.Count == 1 ? (PassphraseMode ? "passphrase" : "password") : (PassphraseMode ? "passphrases" : "passwords"))} · {estimate.Label.ToLowerInvariant()}, {estimate.Bits:0} bits each";
            if (animate)
            {
                UiMotion.Pop(BtnGenerate, 0.96);
                UiMotion.FadeIn(ListPasswords, 6);
            }
        }

        private void ShowList(bool any)
        {
            ListPasswords.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (any == (EmptyStatePanel.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(EmptyStatePanel, !any);
            BtnCopyAll.IsEnabled = any;
            BtnExport.IsEnabled = any;
            BtnShowAll.IsEnabled = any;
        }

        // =================================================================
        //  Row actions
        // =================================================================

        private static PasswordGenItem? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as PasswordGenItem;

        private void BtnToggleVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is { } item) item.IsVisible = !item.IsVisible;
        }

        private void BtnRegenerateOne_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is not { } item) return;
            try
            {
                // The old view had no catch here: with every character type off it crashed the app.
                PasswordGeneratorManager.Fill(item, ReadOptions());
                if (sender is FrameworkElement button) UiMotion.Pop(button, 0.85);
                TxtResultsSubtitle.Text = $"#{item.Number} replaced · {item.StrengthLabel.ToLowerInvariant()}, {item.Estimate.Bits:0} bits";
            }
            catch (ArgumentException ex)
            {
                TxtResultsSubtitle.Text = ex.Message;
                UiMotion.Pop(PreviewPill, 0.88);
            }
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf(sender) is { } item) CopyItem(item);
        }

        private void CopyItem(PasswordGenItem item)
        {
            if (!CopySecret(item.Password))
            {
                TxtResultsSubtitle.Text = "Couldn't copy - another program is using the clipboard. Try again.";
                return;
            }
            if (_copiedItem != null) _copiedItem.JustCopied = false;
            _copiedItem = item;
            item.JustCopied = true;
            _copiedTimer.Stop();
            _copiedTimer.Start();
            TxtResultsSubtitle.Text = $"Copied #{item.Number} - cleared from the clipboard in {ClipboardLifetime.TotalSeconds:0} s, kept out of clipboard history.";
        }

        private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
        {
            if (_results.Count == 0) return;
            if (!CopySecret(string.Join(Environment.NewLine, _results.Select(i => i.Password))))
            {
                TxtResultsSubtitle.Text = "Couldn't copy - another program is using the clipboard. Try again.";
                return;
            }
            UiMotion.Pop(BtnCopyAll, 0.94);
            TxtResultsSubtitle.Text = $"Copied all {_results.Count} - cleared from the clipboard in {ClipboardLifetime.TotalSeconds:0} s.";
        }

        private void BtnShowAll_Click(object sender, RoutedEventArgs e)
        {
            SetShowAll(!_showAll);
            UiMotion.Pop(BtnShowAll, 0.9);
            QueueSave();
        }

        private void SetShowAll(bool show)
        {
            _showAll = show;
            foreach (var item in _results) item.IsVisible = show;
            IconShowAll.Kind = show ? PackIconKind.EyeOffOutline : PackIconKind.EyeOutline;
            BtnShowAll.ToolTip = show ? "Hide all passwords" : "Show all passwords";
        }

        // =================================================================
        //  Clipboard (kept out of history, cleared after 30 s)
        // =================================================================

        /// <summary>
        /// Puts the text on the clipboard marked so Windows doesn't keep it in
        /// clipboard history (Win+V) or sync it to other devices, and so
        /// clipboard managers skip it. Cleared after <see cref="ClipboardLifetime"/>.
        /// </summary>
        private bool CopySecret(string text)
        {
            try
            {
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));   // DWORD 0
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));      // DWORD 0
                Clipboard.SetDataObject(data, true);
                _clipboardSecret = text;
                _clipboardTimer.Stop();
                _clipboardTimer.Start();
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        /// <summary>Clears the clipboard only if it still holds what we copied (never wipes something the user copied since).</summary>
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
        //  Export
        // =================================================================

        private async void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (_results.Count == 0) return;
            if (sender is not MenuItem { Tag: string ext }) return;
            var format = ext == "csv" ? PasswordGenExportFormat.Csv : PasswordGenExportFormat.Text;
            var dialog = new SaveFileDialog
            {
                Title = "Save the passwords (the file isn't encrypted)",
                Filter = format == PasswordGenExportFormat.Csv ? "CSV file - with strength (*.csv)|*.csv" : "Text file - one per line (*.txt)|*.txt",
                FileName = $"passwords-{DateTime.Now:yyyy-MM-dd}",
                DefaultExt = "." + ext,
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(Owner) != true) return;

            var rows = _results.ToList();
            BtnExport.IsEnabled = false;
            try
            {
                await PasswordGeneratorManager.ExportAsync(rows, dialog.FileName, format);
                TxtResultsSubtitle.Text = $"Saved {rows.Count} to {Path.GetFileName(dialog.FileName)} - it isn't encrypted, so delete it once they're stored somewhere safe.";
                UiMotion.Pop(BtnExport, 0.9);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(Owner!, "Couldn't save the file: " + PasswordGeneratorManager.PlainError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnExport.IsEnabled = _results.Count > 0;
            }
        }

        // =================================================================
        //  Settings (how to generate - never a password)
        // =================================================================

        private void ApplySettings(PasswordGenSettings s)
        {
            _applyingSettings = true;
            try
            {
                RadioModeRandom.IsChecked = s.Mode == PasswordGenMode.RandomCharacters;
                RadioModePassphrase.IsChecked = s.Mode == PasswordGenMode.Passphrase;
                SliderLength.Value = Math.Clamp(s.Length, SliderLength.Minimum, SliderLength.Maximum);
                ChkUppercase.IsChecked = s.IncludeUppercase;
                ChkLowercase.IsChecked = s.IncludeLowercase;
                ChkNumbers.IsChecked = s.IncludeNumbers;
                ChkSymbols.IsChecked = s.IncludeSymbols;
                ChkExcludeAmbiguous.IsChecked = s.ExcludeAmbiguous;
                ChkRequireEachCategory.IsChecked = s.RequireEveryType;
                SliderWordCount.Value = Math.Clamp(s.WordCount, SliderWordCount.Minimum, SliderWordCount.Maximum);
                string sepTag = TagFor(s.Separator);
                (SeparatorChips.FirstOrDefault(c => (c.Tag as string) == sepTag) ?? RadioSepDash).IsChecked = true;
                ChkCapitalizeWords.IsChecked = s.CapitalizeWords;
                ChkAppendNumber.IsChecked = s.AppendNumber;
                (BatchChips.FirstOrDefault(c => (c.Tag as string) == s.BatchCount.ToString()) ?? RadioBatch5).IsChecked = true;
                SetShowAll(s.ShowPasswords);
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void SaveSettings() =>
            PasswordGeneratorManager.SaveSettings(new PasswordGenSettings
            {
                Mode = PassphraseMode ? PasswordGenMode.Passphrase : PasswordGenMode.RandomCharacters,
                Length = (int)SliderLength.Value,
                IncludeUppercase = ChkUppercase.IsChecked == true,
                IncludeLowercase = ChkLowercase.IsChecked == true,
                IncludeNumbers = ChkNumbers.IsChecked == true,
                IncludeSymbols = ChkSymbols.IsChecked == true,
                ExcludeAmbiguous = ChkExcludeAmbiguous.IsChecked == true,
                RequireEveryType = ChkRequireEachCategory.IsChecked == true,
                WordCount = (int)SliderWordCount.Value,
                Separator = SelectedSeparator,
                CapitalizeWords = ChkCapitalizeWords.IsChecked == true,
                AppendNumber = ChkAppendNumber.IsChecked == true,
                BatchCount = SelectedBatch,
                ShowPasswords = _showAll,
            });

        private void QueueSave()
        {
            if (Ignore) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }
}
