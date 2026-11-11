using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Bandwidth per App - the shared <see cref="UsageHistoryPage"/> set up with <see cref="BandwidthRecorder"/> and its measures.</summary>
    public partial class BandwidthView : UserControl
    {
        public BandwidthView()
        {
            InitializeComponent();
            Page.Configure(new UsageHistoryConfig
            {
                Recorder = BandwidthRecorder.Instance,
                Metrics = BandwidthManager.Metrics,
                Prefix = "Bandwidth",
                NeedsAdmin = true,
                StateText = StateText,
            });
        }

        private static string StateText(string? startError, bool on) =>
            startError == UsageRecorder.NotAdministrator
                ? Loc.T("Str_Bandwidth_NeedAdmin", "Windows only lets an administrator read network use per program. Restart Smart Tools as administrator.")
                : startError != null
                    ? Loc.F("Str_Bandwidth_StartFailed", "Couldn't start: {0}", startError)
                    : on
                        ? Loc.F("Str_Bandwidth_RecordingOn", "Recording while Smart Tools is open, even when you leave this page. {0:N0} transfers seen so far.", BandwidthRecorder.Instance.EventsSeen)
                        : Loc.T("Str_Bandwidth_RecordingOff", "Not recording. Start it and come back later to see which programs used the most data.");

        /// <summary>Leaving the tool: recording goes on in the background, the history is saved.</summary>
        public void CancelActiveRun() => Page.CancelActiveRun();
    }
}
