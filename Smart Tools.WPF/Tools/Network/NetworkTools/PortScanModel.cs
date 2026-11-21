using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;

namespace Smart_Tools.WPF.Models
{
    public enum ScanTargetKind
    {
        /// <summary>This computer: loopback plus every adapter address it has.</summary>
        ThisPc,

        /// <summary>The other devices on the network one adapter is connected to.</summary>
        Network,

        /// <summary>A host name, an IP address or a range typed by the user.</summary>
        Custom
    }

    /// <summary>
    /// One choice in the Port Scanner's Target card. For <see cref="ScanTargetKind.Network"/>, <see cref="Address"/> is the
    /// adapter's own IPv4 address and <see cref="Prefix"/> its prefix length (what the prefix chips narrow from).
    /// </summary>
    public sealed record ScanTarget(string Id, ScanTargetKind Kind, string Label, string Detail, IPAddress? Address = null, int Prefix = 0, string Adapter = "");

    /// <summary>One address to port-scan; <see cref="Source"/> is the adapter address the connections leave from (null = Windows picks).</summary>
    public sealed record ScanHost(string Label, IPAddress Address, IPAddress? Source);

    /// <summary>What a scan will do: scan <see cref="Hosts"/> as they are, and the live ones found among <see cref="Sweep"/> (when given).</summary>
    public sealed record ScanPlan(string Title, IReadOnlyList<ScanHost> Hosts, IReadOnlyList<IPAddress>? Sweep, IPAddress? SweepSource, string SweepNetwork = "");

    public readonly record struct SweepProgress(int Checked, int Total, int Found);

    /// <summary>The open ports found on one host, shown as a group in the results.</summary>
    public sealed class HostPorts : INotifyPropertyChanged
    {
        private string _status = "";
        private bool _isExpanded = true;

        public HostPorts(string host, string label)
        {
            Host = host;
            Label = label;
        }

        public string Host { get; }

        /// <summary>"Ethernet" for a local address, "" for plain hosts.</summary>
        public string Label { get; }

        public ObservableCollection<PortResult> Ports { get; } = new();

        /// <summary>"scanning...", "3 open", "no open ports".</summary>
        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }

        /// <summary>Whether the host's open ports are shown (the Expander in the results; "Collapse all" sets it for every host).</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
