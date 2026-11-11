using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>Resource Hogs History - the shared <see cref="UsageHistoryPage"/> set up with <see cref="ResourceHogRecorder"/> and its measures.</summary>
    public partial class ResourceHogsView : UserControl
    {
        public ResourceHogsView()
        {
            InitializeComponent();
            Page.Configure(new UsageHistoryConfig
            {
                Recorder = ResourceHogRecorder.Instance,
                Metrics = ResourceHogsManager.Metrics,
                Prefix = "ResourceHogs",
                StateText = (_, on) => on
                    ? Loc.T("Str_ResourceHogs_RecordingOn", "Recording every 5 seconds while Smart Tools is open, even when you leave this page.")
                    : Loc.T("Str_ResourceHogs_RecordingOff", "Not recording. Start it and come back in an hour to see which programs used the most."),
            });
        }

        /// <summary>Leaving the tool: recording goes on in the background, the history is saved.</summary>
        public void CancelActiveRun() => Page.CancelActiveRun();
    }
}
