using Smart_Tools.WPF.Controllers;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The "History recording" section of Settings: choose which history recorders start by themselves when Smart Tools opens
    /// (both, or just one). It writes the same flags the two tools use for their Start / Stop recording button.
    /// </summary>
    public partial class UsageHistorySettingsPanel : UserControl
    {
        /// <summary>A setting was saved; the host shows its "Saved" pill.</summary>
        public event Action? SettingChanged;

        private bool _loading;

        public UsageHistorySettingsPanel()
        {
            InitializeComponent();
        }

        /// <summary>Shows the saved flags. Called when the section opens (the tools may have changed them meanwhile).</summary>
        public void Load()
        {
            _loading = true;
            try
            {
                ChkHogs.IsChecked = JsonSettings.Load<ResourceHogsSettings>(ResourceHogRecorder.SettingsName).Recording;
                ChkBandwidth.IsChecked = JsonSettings.Load<BandwidthSettings>(BandwidthRecorder.SettingsName).Recording;
                SyncAll();
                ShowStatus(null);
            }
            finally { _loading = false; }
        }

        private void SyncAll()
        {
            bool hogs = ChkHogs.IsChecked == true, bandwidth = ChkBandwidth.IsChecked == true;
            ChkAll.IsChecked = hogs && bandwidth ? true : !hogs && !bandwidth ? false : null;
        }

        private void ChkAll_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            // A click on the three-state box goes true -> null -> false; treat "null" as the next step to "on".
            bool on = ChkAll.IsChecked != false;
            ChkHogs.IsChecked = on;
            ChkBandwidth.IsChecked = on;
            ChkAll.IsChecked = on;
            Apply();
        }

        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SyncAll();
            Apply();
        }

        private void Apply()
        {
            bool hogs = ChkHogs.IsChecked == true, bandwidth = ChkBandwidth.IsChecked == true;

            var hogSettings = JsonSettings.Load<ResourceHogsSettings>(ResourceHogRecorder.SettingsName);
            hogSettings.Recording = hogs;
            JsonSettings.Save(ResourceHogRecorder.SettingsName, hogSettings);
            if (hogs) ResourceHogRecorder.Instance.Start();
            else if (ResourceHogRecorder.Instance.IsRunning) ResourceHogRecorder.Instance.Stop();

            var bwSettings = JsonSettings.Load<BandwidthSettings>(BandwidthRecorder.SettingsName);
            bwSettings.Recording = bandwidth;
            JsonSettings.Save(BandwidthRecorder.SettingsName, bwSettings);
            string? problem = null;
            if (bandwidth) problem = BandwidthRecorder.Instance.Start();
            else if (BandwidthRecorder.Instance.IsRunning) BandwidthRecorder.Instance.Stop();
            ShowStatus(problem == "NotAdministrator"
                ? Loc.T("Str_Settings_UhNeedsAdmin", "Bandwidth per App needs administrator rights. It will start by itself the next time you run Smart Tools as administrator.")
                : problem != null ? Loc.F("Str_Bandwidth_StartFailed", "Couldn't start: {0}", problem) : null);

            SettingChanged?.Invoke();
        }

        private void ShowStatus(string? text)
        {
            TxtStatus.Text = text ?? "";
            TxtStatus.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
