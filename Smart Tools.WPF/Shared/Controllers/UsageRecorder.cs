namespace Smart_Tools.WPF.Controllers
{
    /// <summary>What a history tool remembers (%AppData%\Smart Tools\&lt;SettingsName&gt;.json): whether it records, and what the page shows.</summary>
    public sealed class UsageSettings
    {
        public bool Recording { get; set; }
        public int PeriodHours { get; set; } = 3;

        /// <summary>Name of the chosen <see cref="UsageMetric"/> (the tool's first one when empty or unknown).</summary>
        public string Metric { get; set; } = "";

        /// <summary>Show a notification when one program stays above <see cref="AlertLimit"/> (the recorder says in what unit).</summary>
        public bool AlertOn { get; set; }

        /// <summary>0 = the recorder's own default.</summary>
        public double AlertLimit { get; set; }
    }

    /// <summary>
    /// A background recorder of per-program usage for the history tools. It owns the saved history (<see cref="Store"/>) and the on/off
    /// flag; a subclass says how to take the readings (<see cref="Start"/>, <see cref="StopCore"/>, <see cref="Flush"/>).
    /// </summary>
    public abstract class UsageRecorder
    {
        /// <summary>What <see cref="Start"/> returns when the tool needs administrator rights and doesn't have them.</summary>
        public const string NotAdministrator = "NotAdministrator";

        /// <summary>Guards the subclass's own start/stop state and the one-time history load.</summary>
        protected readonly object Gate = new();

        private bool _loaded;
        private DateTime _lastSave = DateTime.UtcNow;

        protected UsageRecorder(string settingsName, string historyName, int slotCount)
        {
            SettingsName = settingsName;
            Store = new AppUsageStore(historyName, slotCount, TimeSpan.FromHours(24));
        }

        public string SettingsName { get; }

        // ---- "tell me when a program goes over a limit" and the tray tooltip

        /// <summary>The unit of the alert limit ("% CPU", "MB/s"); empty when the recorder has no alert.</summary>
        public virtual string AlertUnit => "";

        /// <summary>The limit used until the person types one.</summary>
        public virtual double AlertDefaultLimit => 0;

        /// <summary>A few words on the busiest program right now ("chrome 34 % CPU"), for the tray icon's tooltip. Empty when not recording.</summary>
        public string TopSummary { get; protected set; } = "";

        private bool _alertOn, _alertLoaded;
        private double _alertLimit;
        private readonly Dictionary<string, int> _over = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastAlert = new(StringComparer.OrdinalIgnoreCase);
        private const int ReadingsOver = 3;                                // 3 readings in a row (15 s), so a short spike is ignored
        private static readonly TimeSpan AlertPause = TimeSpan.FromMinutes(10);   // the same program is not announced again for a while

        /// <summary>Reads the alert switch and limit from the saved settings. Called at start and whenever they are saved.</summary>
        protected void RefreshAlertSettings()
        {
            _alertLoaded = true;
            try
            {
                var s = LoadSettings();
                _alertOn = s.AlertOn && AlertUnit.Length > 0;
                _alertLimit = s.AlertLimit > 0 ? s.AlertLimit : AlertDefaultLimit;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _alertOn = false; }
        }

        /// <summary>Call once per program per reading with the number the limit is about; shows one toast after it stays over the limit.</summary>
        protected void CheckAlert(string app, double value)
        {
            if (!_alertLoaded) RefreshAlertSettings();
            if (!_alertOn || _alertLimit <= 0) return;
            if (value < _alertLimit) { _over.Remove(app); return; }

            _over[app] = _over.GetValueOrDefault(app) + 1;
            if (_over[app] < ReadingsOver) return;
            if (_lastAlert.TryGetValue(app, out var last) && DateTime.UtcNow - last < AlertPause) return;

            _lastAlert[app] = DateTime.UtcNow;
            _over[app] = 0;
            ToastNotificationManager.ShowToast(
                Loc.T("Str_UhAlert_Title", "High usage"),
                Loc.F("Str_UhAlert_Body", "{0} has been above {1} for a while.", app, _alertLimit.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + " " + AlertUnit));
        }

        /// <summary>Forgets which programs were over the limit (a new recording starts clean).</summary>
        protected void ResetAlerts()
        {
            _over.Clear();
            TopSummary = "";
        }

        public AppUsageStore Store { get; }

        /// <summary>The tool's catalog id: its name is the language key Str_Tool_&lt;ToolId&gt;_Name.</summary>
        public abstract string ToolId { get; }

        /// <summary>Language key of the short explanation shown under the tool in Settings > History recording.</summary>
        public abstract string SettingsNoteKey { get; }

        public abstract bool IsRunning { get; }

        /// <summary>Starts recording. Returns null, or why it could not (<see cref="NotAdministrator"/>, or a message).</summary>
        public abstract string? Start();

        /// <summary>Ends the readings (called with no lock held; the subclass locks <see cref="Gate"/> itself).</summary>
        protected abstract void StopCore();

        /// <summary>Hands what was counted since the last reading to the store.</summary>
        protected virtual void Flush() { }

        /// <summary>Stops and writes the history in the background (the file can be several MB: never on the screen's thread).</summary>
        public void Stop()
        {
            StopCore();
            Task.Run(FinishStop);
        }

        /// <summary>Stops and writes the history before returning (app exit: the process may end right after).</summary>
        public void StopNow()
        {
            StopCore();
            FinishStop();
        }

        protected void FinishStop()
        {
            Flush();
            Store.Save();
        }

        /// <summary>Writes the history if the last write was more than two minutes ago.</summary>
        protected void SaveIfDue()
        {
            if (DateTime.UtcNow - _lastSave <= TimeSpan.FromMinutes(2)) return;
            _lastSave = DateTime.UtcNow;
            Store.Save();
        }

        /// <summary>Loads the saved history once (so a page can show it before recording starts).</summary>
        public void EnsureLoaded()
        {
            lock (Gate)
            {
                if (_loaded) return;
                Store.Load();
                _loaded = true;
            }
        }

        /// <summary>Same, without holding up the caller (reading a day of history takes about half a second).</summary>
        public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : Task.Run(EnsureLoaded);

        public UsageSettings LoadSettings() => JsonSettings.Load<UsageSettings>(SettingsName);

        public void SaveSettings(UsageSettings settings)
        {
            JsonSettings.Save(SettingsName, settings);
            RefreshAlertSettings();
        }
    }
}
