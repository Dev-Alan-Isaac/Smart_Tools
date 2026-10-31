using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Modules;
using System.Windows;
using Application = System.Windows.Application;

namespace Smart_Tools.WPF
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Started by Windows with "start with Windows": open straight into the tray.</summary>
        public static bool StartInTray { get; private set; }

        private const string ActivateEventName = @"Local\SmartTools.Activate";
        private Mutex? _instanceMutex;
        private EventWaitHandle? _activateEvent;

        private void ListenForActivation()
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            var listener = new Thread(() =>
            {
                while (_activateEvent.WaitOne())
                    Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromOutside());
            })
            { IsBackground = true, Name = "SingleInstanceListener" };
            listener.Start();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            Log.Init();
            HookGlobalErrorLogging();
            Log.Info($"Starting Smart Tools {typeof(App).Assembly.GetName().Version}, pid {Environment.ProcessId}, {Environment.OSVersion.VersionString}, {Environment.ProcessorCount} cores, args [{string.Join(' ', e.Args)}]");

            StartInTray = e.Args.Contains(ShortcutManager.StartInTrayArgument, StringComparer.OrdinalIgnoreCase);

            // Started by "Restart as administrator": let the old window finish closing (and save
            // its settings) before this one reads them and opens - base.OnStartup shows the window.
            AppRestartManager.WaitForPreviousInstance(e.Args);

            // Only one copy may run (otherwise each puts its own icon in the tray): a second launch
            // asks the first to come forward and exits.
            _instanceMutex = new Mutex(true, @"Local\SmartTools.SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                try
                {
                    using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                    activate.Set();
                }
                catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or System.IO.IOException)
                {
                    Log.Info($"Single instance: {ex.Message}");
                }
                Log.Info("Another copy is already running: asked it to come forward, exiting");
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
                return;
            }
            CheckPreviousSession();
            ListenForActivation();

            // Tool and section names come from the language dictionaries (Str_Tool_*, Str_Cat_*); the model itself stays WPF-free.
            Smart_Tools.WPF.Models.CatalogText.Lookup = key => Current.TryFindResource(key) as string;

            base.OnStartup(e);
            ResourceMonitor.Start(Dispatcher);
            // The saved theme is applied (guarded) by MainWindow's constructor before it shows.
        }

        // A marker file exists while the app runs and is deleted on a clean exit. Finding it at startup
        // means the last session ended in a crash, kill or hard reset: log that with its last heartbeat.
        private static string MarkerPath => System.IO.Path.Combine(Log.Folder, "running.marker");

        private static void CheckPreviousSession()
        {
            try
            {
                if (System.IO.File.Exists(MarkerPath))
                {
                    string started = System.IO.File.ReadAllText(MarkerPath).Trim();
                    string lastBeat = "";
                    try
                    {
                        lastBeat = System.IO.File.ReadLines(DurableLog.FilePath).LastOrDefault() ?? "";
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                    {
                        // no heartbeat file yet
                    }
                    Log.Warn($"Previous session did NOT exit cleanly (started {started}). Last heartbeat line: {lastBeat}");
                }
                System.IO.File.WriteAllText(MarkerPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} pid {Environment.ProcessId}");
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't check/write the session marker", ex);
            }
        }

        private void HookGlobalErrorLogging()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                // Logged, then swallowed: one failed click or async void callback shouldn't close the whole
                // app and every other tool's unsaved state with it.
                Log.Error("Unhandled UI exception", args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Fatal($"Unhandled exception (terminating={args.IsTerminating})", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
                Log.Error("Unobserved task exception", args.Exception);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Info($"Exiting, code {e.ApplicationExitCode}");
            // Catch-all save on close -- covers any in-place edits (e.g.
            // renaming a category or pattern) that don't go through one of
            // the explicit add/remove/save actions that already call
            // AppSettingsStore.Save() themselves.
            if (_instanceMutex != null)   // null = a duplicate launch that never started: must not overwrite the real one's settings
            {
                AppSettingsStore.Save();
                ShortcutManager.Shutdown();
                _activateEvent?.Dispose();
                _instanceMutex.Dispose();
                ResourceMonitor.Stop();
                try { System.IO.File.Delete(MarkerPath); } catch (System.IO.IOException) { }
            }
            base.OnExit(e);
        }
    }

}
