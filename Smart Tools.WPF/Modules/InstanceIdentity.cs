// Root namespace on purpose (like AppPaths and AppMode): every sub-namespace sees it without an extra using.
namespace Smart_Tools.WPF
{
    /// <summary>
    /// The names that tell one running copy of the app apart from another. The installed copy and a Visual Studio /
    /// debug copy use different ones, so they can run side by side (to compare a change with the real app) without
    /// sharing a single-instance lock, a "come forward" signal or - above all - the same settings files.
    /// Pure on purpose: the decision lives here, the callers only ask.
    /// </summary>
    public static class InstanceIdentity
    {
        /// <summary>The per-session lock a running copy holds; a second launch of the same kind sees it and steps aside.</summary>
        public static string MutexName(bool development) =>
            development ? @"Local\SmartTools.Dev.SingleInstance" : @"Local\SmartTools.SingleInstance";

        /// <summary>The signal a second launch sets so the first one comes to the front.</summary>
        public static string ActivateEventName(bool development) =>
            development ? @"Local\SmartTools.Dev.Activate" : @"Local\SmartTools.Activate";

        /// <summary>The folder name under %AppData% / %LocalAppData% that holds settings, logs and caches.</summary>
        public static string DataFolderName(bool development) =>
            development ? "Smart Tools (Dev)" : "Smart Tools";

        /// <summary>True for a copy run from a build folder or under a debugger.</summary>
        public static bool IsDevelopment => AppMode.Current == AppRunMode.Development;
    }
}
