namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The history recorders. Add a new history tool's recorder to <see cref="All"/> and Settings, app start and exit pick it up.</summary>
    public static class UsageRecorders
    {
        public static IReadOnlyList<UsageRecorder> All { get; } = new UsageRecorder[]
        {
            ResourceHogRecorder.Instance,
            BandwidthRecorder.Instance,
        };

        /// <summary>Starts the recorders the user switched on last time. Never throws: a recorder must not stop Smart Tools from opening.</summary>
        public static void StartIfEnabled()
        {
            foreach (var recorder in All)
            {
                try
                {
                    if (recorder.LoadSettings().Recording) recorder.Start();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Warn("Could not start the history recorder " + recorder.ToolId + ": " + ex.Message);
                }
            }
        }

        /// <summary>At exit: stops all (and the network trace session) and writes the history before the process ends.</summary>
        public static void StopAll()
        {
            foreach (var recorder in All) recorder.StopNow();
        }
    }
}
