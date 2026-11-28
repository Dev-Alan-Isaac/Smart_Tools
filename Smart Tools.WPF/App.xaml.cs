using Smart_Tools.WPF.Views;
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

        private Mutex? _instanceMutex;
        private EventWaitHandle? _activateEvent;

        private void ListenForActivation()
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceIdentity.ActivateEventName(InstanceIdentity.IsDevelopment));
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
            // How this copy is used decides where its data lives, so it is settled first - before Log.Init
            // (which creates the data folder) and before anything reads AppPaths.
            var mode = AppMode.Detect(e.Args);
            if (e.Args.Contains(AppMode.UninstallArgument, StringComparer.OrdinalIgnoreCase))
            {
                RunUninstall(mode);
                return;
            }
            if (mode == AppRunMode.Ask && !AskInstallOrRun()) return;

            Log.Init();
            RenderRecovery.Init(e.Args);
            HookGlobalErrorLogging();
            Log.Info($"Starting Smart Tools {AppMode.VersionText()} ({AppMode.Current}), pid {Environment.ProcessId}, {Environment.OSVersion.VersionString}, {Environment.ProcessorCount} cores, args [{string.Join(' ', e.Args)}]");

            StartInTray = e.Args.Contains(ShortcutManager.StartInTrayArgument, StringComparer.OrdinalIgnoreCase);

            // Started by "Restart as administrator": let the old window finish closing (and save
            // its settings) before this one reads them and opens - base.OnStartup shows the window.
            AppRestartManager.WaitForPreviousInstance(e.Args);

            // Only one copy of each kind may run (otherwise each puts its own icon in the tray): a second launch
            // asks the first to come forward and exits. A debug copy has its own lock and data folder, so it runs beside the installed one.
            _instanceMutex = new Mutex(true, InstanceIdentity.MutexName(InstanceIdentity.IsDevelopment), out bool isFirst);
            if (!isFirst)
            {
                try
                {
                    using var activate = EventWaitHandle.OpenExisting(InstanceIdentity.ActivateEventName(InstanceIdentity.IsDevelopment));
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

            ImageLoad.ConfigureLimits();
            base.OnStartup(e);
            ResourceMonitor.Start(Dispatcher);
            UsageRecorders.StartIfEnabled();

            // "Start with Windows" stores this exe's path: write it again from where the app really is now, so moving the
            // app (or switching between portable and installed) never leaves an entry that points at a folder that is gone.
            // A Visual Studio build must not rewrite it every time it runs.
            if (AppMode.Current != AppRunMode.Development && Modules.AppSettingsStore.StartWithWindows)
                ShortcutManager.ApplyStartWithWindows(true);
            // The saved theme is applied (guarded) by MainWindow's constructor before it shows.
        }

        /// <summary>The Install / Run question for a brand-new profile. False when the app should not go on (installed elsewhere, or closed).</summary>
        private bool AskInstallOrRun()
        {
            // The question window is the only window for a moment: closing it must not end the app before the main window exists.
            var shutdownMode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new Smart_Tools.WPF.Views.StartupChoiceWindow();
            window.ShowDialog();
            ShutdownMode = shutdownMode;

            if (window.Result == Smart_Tools.WPF.Views.StartupChoiceWindow.Outcome.Run) return true;
            Shutdown();   // Installed: the installed copy was started and takes over. Cancelled: nothing was written.
            return false;
        }

        /// <summary>Started as "SmartTools.exe --uninstall" (from Apps &amp; features or Settings): asks, removes, and exits.</summary>
        private void RunUninstall(AppRunMode mode)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string T(string en, string es) => Smart_Tools.WPF.Views.WindowsPalette.T(en, es);

            if (mode != AppRunMode.Installed)
            {
                System.Windows.MessageBox.Show(T("This copy of Smart Tools was not installed by the app, so there is nothing to uninstall here.",
                                                 "Esta copia de Smart Tools no fue instalada por la aplicación, así que no hay nada que desinstalar aquí."),
                    AppMode.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (AppInstaller.IsAnotherCopyRunning())
            {
                System.Windows.MessageBox.Show(T("Close Smart Tools first (check the tray icon), then uninstall again.",
                                                 "Cierra Smart Tools primero (revisa el ícono de la bandeja) y vuelve a desinstalar."),
                    AppMode.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (System.Windows.MessageBox.Show(T("Uninstall Smart Tools from this PC?", "¿Desinstalar Smart Tools de este equipo?"),
                         AppMode.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                bool deleteData = System.Windows.MessageBox.Show(
                    T("Also delete your settings and downloaded models?\n\n" + AppMode.InstalledDataFolder + "\n\nChoose No to keep them in case you install again.",
                      "¿Borrar también tus ajustes y modelos descargados?\n\n" + AppMode.InstalledDataFolder + "\n\nElige No para conservarlos por si vuelves a instalar."),
                    AppMode.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
                AppInstaller.Uninstall(deleteData);
            }
            Shutdown();
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
                // app and every other tool's unsaved state with it. Drawing that broke for good (graphics reset after
                // sleep) would otherwise leave a frozen picture forever: RenderRecovery restarts or switches to software.
                args.Handled = true;
                if (RenderRecovery.TryHandle(args.Exception)) return;
                Log.Error("Unhandled UI exception", args.Exception);
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
                UsageRecorders.StopAll();
                try { System.IO.File.Delete(MarkerPath); } catch (System.IO.IOException) { }
            }
            base.OnExit(e);
        }
    }

}
