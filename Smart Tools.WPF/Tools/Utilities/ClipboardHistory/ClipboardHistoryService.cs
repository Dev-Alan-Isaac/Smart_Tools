using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Modules;
using System.Windows.Threading;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The one Clipboard History shared by the tool and the Settings options, so the list is the same whether it was
    /// recorded with the tool open or not. Recording runs while the tool has been opened or "start recording when
    /// Smart Tools opens" is on. With "keep history between sessions" on, the list is saved (see
    /// <see cref="ClipboardHistoryStorage"/>) after each change and put back at the next start. Call on the UI thread.
    /// </summary>
    public static class ClipboardHistoryService
    {
        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

        private static ClipboardHistoryManager? _manager;
        private static DispatcherTimer? _saveTimer;
        private static bool _toolOpened;

        // Every disk write (save or delete) takes a ticket; a save whose ticket is no longer the latest is skipped,
        // so a slow background save can never bring back a deleted file or overwrite a newer one.
        private static readonly object DiskLock = new();
        private static long _diskTicket;

        public static ClipboardHistoryManager Instance => _manager
            ?? throw new InvalidOperationException("ClipboardHistoryService.Initialize() must run at startup, before the Clipboard History tool is used.");

        /// <summary>Creates the shared history and, with "keep history" on, puts back the saved list. Call once at startup.</summary>
        public static void Initialize()
        {
            if (_manager != null) return;
            _manager = new ClipboardHistoryManager();
            _manager.Changed += OnChanged;
            if (!AppSettingsStore.ClipboardKeepHistory) return;
            try
            {
                _manager.Restore(ClipboardHistoryStorage.Load());
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't restore the saved clipboard history", ex);   // a bad file must never stop the app starting
            }
        }

        /// <summary>The tool page was shown: record from now on, even if the setting is off.</summary>
        public static void ToolOpened()
        {
            _toolOpened = true;
            Instance.Start();
        }

        /// <summary>
        /// Applies "start recording when Smart Tools opens": at startup and when it is changed in Settings. Without
        /// that setting or an opened tool nothing is listening, and unless "keep history" is on the list is dropped.
        /// </summary>
        public static void ApplyBackgroundSetting()
        {
            if (_toolOpened || AppSettingsStore.ClipboardBackgroundRecording)
            {
                Instance.Start();
                return;
            }
            Instance.Stop();
            if (!AppSettingsStore.ClipboardKeepHistory) Instance.ClearAll();
        }

        /// <summary>
        /// Applies "keep history between sessions": saves what is there now, or deletes the saved copy (and, when
        /// nothing is recording, the list with it, as <see cref="ApplyBackgroundSetting"/> does).
        /// </summary>
        public static void ApplyKeepHistorySetting()
        {
            if (AppSettingsStore.ClipboardKeepHistory)
            {
                SaveNow();
                return;
            }
            _saveTimer?.Stop();
            DeleteSaved();
            ApplyBackgroundSetting();
        }

        /// <summary>Forgets everything, in memory and on disk.</summary>
        public static void ClearAll()
        {
            _manager?.ClearAll();   // raises Changed, which would schedule a save: stop that after, not before
            _saveTimer?.Stop();
            DeleteSaved();
        }

        /// <summary>The app is closing: save the list if asked to, then stop.</summary>
        public static void Shutdown()
        {
            if (_manager == null) return;
            _saveTimer?.Stop();
            _manager.Changed -= OnChanged;
            if (AppSettingsStore.ClipboardKeepHistory) Persist(NextTicket(), _manager.Snapshot());
            _manager.Dispose();
            _manager = null;
        }

        private static void OnChanged()
        {
            if (!AppSettingsStore.ClipboardKeepHistory) return;
            _saveTimer ??= CreateTimer();
            _saveTimer.Stop();   // restart: a burst of copies is saved once
            _saveTimer.Start();
        }

        private static DispatcherTimer CreateTimer()
        {
            var timer = new DispatcherTimer { Interval = SaveDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SaveNow();
            };
            return timer;
        }

        private static void SaveNow()
        {
            if (_manager == null) return;
            long ticket = NextTicket();
            var items = _manager.Snapshot();
            _ = Task.Run(() => Persist(ticket, items));
        }

        private static long NextTicket() => Interlocked.Increment(ref _diskTicket);

        private static void Persist(long ticket, List<ClipboardItem> items)
        {
            lock (DiskLock)
                if (ticket == Interlocked.Read(ref _diskTicket)) ClipboardHistoryStorage.Save(items);
        }

        private static void DeleteSaved()
        {
            NextTicket();   // cancels any save still waiting to run
            lock (DiskLock) ClipboardHistoryStorage.Delete();
        }
    }
}
