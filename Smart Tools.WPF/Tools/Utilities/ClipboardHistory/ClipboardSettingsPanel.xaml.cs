using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Modules;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>The Clipboard history section of Settings - UI only. What the options do is in <see cref="ClipboardHistoryService"/>.</summary>
    public partial class ClipboardSettingsPanel : UserControl
    {
        /// <summary>A setting was changed in <see cref="AppSettingsStore"/>; the host saves and shows its "Saved" pill.</summary>
        public event Action? SettingChanged;

        public ClipboardSettingsPanel() => InitializeComponent();

        /// <summary>Shows the current settings. Clicks only fire for the user, so nothing needs guarding while this runs.</summary>
        public void Load()
        {
            ChkClipboardBackground.IsChecked = AppSettingsStore.ClipboardBackgroundRecording;
            ChkClipboardKeep.IsChecked = AppSettingsStore.ClipboardKeepHistory;
        }

        private void ChkClipboardBackground_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsStore.ClipboardBackgroundRecording = ChkClipboardBackground.IsChecked == true;
            ClipboardHistoryService.ApplyBackgroundSetting();
            SettingChanged?.Invoke();
        }

        private void ChkClipboardKeep_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsStore.ClipboardKeepHistory = ChkClipboardKeep.IsChecked == true;
            ClipboardHistoryService.ApplyKeepHistorySetting();
            SettingChanged?.Invoke();
        }

        private void BtnClipboardDelete_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(
                Window.GetWindow(this),
                Str("Str_Settings_ClipboardDeleteText", "Delete the whole clipboard history, including pinned items and the saved copy?"),
                Str("Str_Settings_ClipboardDelete", "Delete history"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) ClipboardHistoryService.ClearAll();
        }

        private string Str(string key, string fallback) => TryFindResource(key) as string ?? fallback;
    }
}
