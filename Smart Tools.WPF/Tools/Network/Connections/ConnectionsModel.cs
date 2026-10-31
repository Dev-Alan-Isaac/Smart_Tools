using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    /// <summary>Which connections the Connections list shows.</summary>
    public enum ConnectionScope { Outside, Local, All }

    /// <summary>
    /// One endpoint of this PC and the app that owns it, as read at one moment. <see cref="Key"/> tells two readings
    /// of the same connection apart from a different one.
    /// </summary>
    public sealed record ConnectionInfo(
        string Key,
        int Pid,
        string AppName,
        string Protocol,
        string LocalAddress,
        int LocalPort,
        string RemoteAddress,
        int RemotePort,
        string State,
        bool IsOutside,
        bool IsListening);

    /// <summary>A row in the list: the reading plus the bits that change while it stays on screen.</summary>
    public sealed class ConnectionRow : INotifyPropertyChanged
    {
        private string _host = "";
        private bool _isNew;

        public ConnectionRow(ConnectionInfo info) => Info = info;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ConnectionInfo Info { get; }

        public string AppName => Info.AppName;
        public int Pid => Info.Pid;
        public string Protocol => Info.Protocol;
        public string State => Info.State;
        public bool IsOutside => Info.IsOutside;
        public int RemotePort => Info.RemotePort;
        public string RemotePortText => Info.RemotePort > 0 ? Info.RemotePort.ToString() : "";
        public string RemoteAddress => Info.RemoteAddress;
        public string LocalText => Info.LocalAddress + ":" + Info.LocalPort;

        /// <summary>Remote host name from a reverse lookup. Empty until asked for, and when there is none.</summary>
        public string Host
        {
            get => _host;
            set
            {
                if (_host == value) return;
                _host = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Host)));
            }
        }

        /// <summary>First seen in the latest reading (not on the very first one).</summary>
        public bool IsNew
        {
            get => _isNew;
            set
            {
                if (_isNew == value) return;
                _isNew = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNew)));
            }
        }
    }

    /// <summary>How the tool is set up. Never holds addresses, app names or host names.</summary>
    public sealed class ConnectionsSettings
    {
        public ConnectionScope Scope { get; set; } = ConnectionScope.Outside;
        public bool HideListening { get; set; } = true;

        /// <summary>0 = paused.</summary>
        public int RefreshSeconds { get; set; } = 5;

        public bool LookUpNames { get; set; }
    }
}
