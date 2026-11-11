using Smart_Tools.WPF.Controllers;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The "History recording" section of Settings: choose which history recorders start by themselves when Smart Tools opens
    /// (all, or just some). One tick per recorder in <see cref="UsageRecorders.All"/>; it writes the same flag each tool uses for its
    /// Start / Stop recording button.
    /// </summary>
    public partial class UsageHistorySettingsPanel : UserControl
    {
        /// <summary>A setting was saved; the host shows its "Saved" pill.</summary>
        public event Action? SettingChanged;

        private readonly List<(UsageRecorder Recorder, CheckBox Box)> _tools = new();
        private bool _loading, _someOn;   // _someOn: the "all" box is showing the in-between state

        public UsageHistorySettingsPanel()
        {
            InitializeComponent();
            foreach (var recorder in UsageRecorders.All)
            {
                var box = new CheckBox { Margin = new Thickness(0, _tools.Count == 0 ? 0 : 14, 0, 0) };
                box.SetResourceReference(ContentControl.ContentProperty, "Str_Tool_" + recorder.ToolId + "_Name");
                box.Click += Tool_Click;
                var note = new TextBlock { Margin = new Thickness(28, 2, 0, 0), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                note.SetResourceReference(TextBlock.TextProperty, recorder.SettingsNoteKey);
                ToolList.Children.Add(box);
                ToolList.Children.Add(note);
                _tools.Add((recorder, box));
            }
        }

        /// <summary>Shows the saved flags. Called when the section opens (the tools may have changed them meanwhile).</summary>
        public void Load()
        {
            _loading = true;
            try
            {
                foreach (var (recorder, box) in _tools) box.IsChecked = recorder.LoadSettings().Recording;
                SyncAll();
                ShowStatus(null);
            }
            finally { _loading = false; }
        }

        private void SyncAll()
        {
            int on = _tools.Count(t => t.Box.IsChecked == true);
            _someOn = on > 0 && on < _tools.Count;
            ChkAll.IsChecked = on == _tools.Count ? true : on == 0 ? false : null;
        }

        private void ChkAll_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            // A two-state box that SyncAll can show as "some" (null). WPF turns "some" into off on a click, but the user means "all":
            // so some -> all on, off -> all on, all on -> all off.
            bool on = _someOn || ChkAll.IsChecked == true;
            foreach (var (_, box) in _tools) box.IsChecked = on;
            ChkAll.IsChecked = on;
            _someOn = false;
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
            string? problem = null;
            foreach (var (recorder, box) in _tools)
            {
                bool wanted = box.IsChecked == true;
                var settings = recorder.LoadSettings();
                settings.Recording = wanted;
                recorder.SaveSettings(settings);
                if (wanted) { string? failed = recorder.Start(); problem ??= failed; }
                else if (recorder.IsRunning) recorder.Stop();
            }
            ShowStatus(problem == UsageRecorder.NotAdministrator
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
