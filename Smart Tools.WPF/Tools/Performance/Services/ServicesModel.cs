using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    /// <summary>Where a Windows service is in its life cycle. The "pending" states last a moment while it starts or stops.</summary>
    public enum WinServiceState { Stopped, StartPending, StopPending, Running, ContinuePending, PausePending, Paused, Unknown }

    /// <summary>How Windows starts a service.</summary>
    public enum WinServiceStartMode { Automatic, AutomaticDelayed, Manual, Disabled }

    public enum WinServiceChangeResult { Done, NeedsAdministrator, HasRunningDependents, Disabled, TimedOut, Failed }

    /// <summary>One Windows service. State and startup type change while the tool is open, so they raise change notifications.</summary>
    public sealed class WinService : INotifyPropertyChanged
    {
        private WinServiceState _state;
        private WinServiceStartMode _startMode;
        private bool _acceptsStop, _acceptsPause, _isBusy;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>The short name Windows uses (for example "Spooler").</summary>
        public string Name { get; init; } = "";

        public string DisplayName { get; init; } = "";

        public string Description { get; init; } = "";

        /// <summary>The command line of the program that runs the service.</summary>
        public string BinaryPath { get; init; } = "";

        /// <summary>The account the service runs as (for example "LocalSystem").</summary>
        public string Account { get; init; } = "";

        /// <summary>A service Windows itself depends on; the tool asks twice before stopping or disabling it.</summary>
        public bool IsCore { get; init; }

        public string Subtitle => Description.Length > 0 ? Description : Name;

        public WinServiceState State
        {
            get => _state;
            set { if (_state != value) { _state = value; RaiseAll(); } }
        }

        public WinServiceStartMode StartMode
        {
            get => _startMode;
            set { if (_startMode != value) { _startMode = value; RaiseAll(); } }
        }

        /// <summary>Whether the service accepts a stop request. Windows only reports this while the service runs.</summary>
        public bool AcceptsStop
        {
            get => _acceptsStop;
            set { if (_acceptsStop != value) { _acceptsStop = value; RaiseAll(); } }
        }

        public bool AcceptsPause
        {
            get => _acceptsPause;
            set { if (_acceptsPause != value) { _acceptsPause = value; RaiseAll(); } }
        }

        /// <summary>True while a start, stop, or other change on this service is in progress.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; RaiseAll(); } }
        }

        public bool IsRunning => _state == WinServiceState.Running;

        public bool IsPaused => _state == WinServiceState.Paused;

        public bool CanStart => !_isBusy && _state == WinServiceState.Stopped && _startMode != WinServiceStartMode.Disabled;

        public bool CanStop => !_isBusy && _acceptsStop && _state is WinServiceState.Running or WinServiceState.Paused;

        public bool CanRestart => !_isBusy && _acceptsStop && _state == WinServiceState.Running;

        public bool CanOpenMenu => !_isBusy;

        /// <summary>Takes the live values (state, startup type, accepted controls) from a fresh reading of the same service.</summary>
        public void UpdateFrom(WinService fresh)
        {
            _state = fresh._state;
            _startMode = fresh._startMode;
            _acceptsStop = fresh._acceptsStop;
            _acceptsPause = fresh._acceptsPause;
            RaiseAll();
        }

        private void RaiseAll()
        {
            foreach (string name in Notified)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static readonly string[] Notified =
        {
            nameof(State), nameof(StartMode), nameof(AcceptsStop), nameof(AcceptsPause), nameof(IsBusy),
            nameof(IsRunning), nameof(IsPaused), nameof(CanStart), nameof(CanStop), nameof(CanRestart), nameof(CanOpenMenu),
        };
    }
}
