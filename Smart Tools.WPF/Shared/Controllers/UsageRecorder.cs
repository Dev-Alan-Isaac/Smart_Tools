namespace Smart_Tools.WPF.Controllers
{
    /// <summary>What a history tool remembers (%AppData%\Smart Tools\&lt;SettingsName&gt;.json): whether it records, and what the page shows.</summary>
    public sealed class UsageSettings
    {
        public bool Recording { get; set; }
        public int PeriodHours { get; set; } = 3;

        /// <summary>Name of the chosen <see cref="UsageMetric"/> (the tool's first one when empty or unknown).</summary>
        public string Metric { get; set; } = "";
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

        public void SaveSettings(UsageSettings settings) => JsonSettings.Save(SettingsName, settings);
    }
}
