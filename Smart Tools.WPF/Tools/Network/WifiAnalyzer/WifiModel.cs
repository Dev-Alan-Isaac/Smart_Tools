using System.ComponentModel;
using System.Globalization;

namespace Smart_Tools.WPF.Models
{
    public enum WifiBand { Unknown, Ghz24, Ghz5, Ghz6 }

    /// <summary>One access point heard in a scan (one BSSID). A network (SSID) can have several.</summary>
    public sealed class WifiNetwork : INotifyPropertyChanged
    {
        private bool _isConnected;

        public required string Ssid { get; init; }
        public required string Bssid { get; init; }
        /// <summary>Signal in dBm, e.g. -58.</summary>
        public int Rssi { get; init; }
        /// <summary>Windows' 0-100 signal quality.</summary>
        public int Quality { get; init; }
        public int FrequencyMhz { get; init; }
        public int Channel { get; init; }
        public WifiBand Band { get; init; }
        public string Standard { get; init; } = "";
        public string Security { get; init; } = "";
        public string Vendor { get; set; } = "";

        public bool IsHidden => Ssid.Length == 0;
        public string DisplayName => IsHidden ? "(hidden network)" : Ssid;
        public string BandText => WifiText.BandName(Band);
        public string SignalText => $"{Rssi} dBm";
        public string QualityText => WifiText.SignalLabel(Rssi);
        public string ChannelText => Channel > 0 ? Channel.ToString(CultureInfo.InvariantCulture) : "";
        public string AccessPoint => Vendor.Length > 0 ? $"{Bssid} · {Vendor}" : Bssid;

        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (_isConnected == value) return;
                _isConnected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>The link the PC has right now.</summary>
    public sealed record WifiLink(string Ssid, string Bssid, string Standard, int Quality, long RxKbps, long TxKbps, string ProfileName);

    public sealed class WifiScan
    {
        public List<WifiNetwork> Networks { get; init; } = new();
        public WifiLink? Link { get; init; }
        public int AdapterCount { get; init; }
        public string AdapterName { get; init; } = "";
        /// <summary>A sentence for the user when the scan couldn't run or found nothing.</summary>
        public string? Problem { get; init; }
        public DateTime When { get; init; } = DateTime.Now;
    }

    /// <summary>A Wi-Fi profile saved in Windows.</summary>
    public sealed class WifiProfile
    {
        public required string Name { get; init; }
        public string Authentication { get; init; } = "";
        public string Encryption { get; init; } = "";
        public bool AutoConnect { get; init; }
        public bool IsOpen => Authentication.Equals("open", StringComparison.OrdinalIgnoreCase) && Encryption.Equals("none", StringComparison.OrdinalIgnoreCase);
        public string SecurityText => WifiText.ProfileSecurity(Authentication, Encryption);
        public string ConnectText => AutoConnect ? "Automatically" : "Manually";
        public string PasswordText => IsOpen ? "No password" : "Hidden";
    }

    /// <summary>Why one channel is recommended over the others.</summary>
    public sealed record ChannelAdvice(WifiBand Band, int Channel, double Score, int NetworksOnIt, string Reason);

    public static class WifiText
    {
        public static string BandName(WifiBand b) => b switch
        {
            WifiBand.Ghz24 => "2.4 GHz",
            WifiBand.Ghz5 => "5 GHz",
            WifiBand.Ghz6 => "6 GHz",
            _ => "",
        };

        public static string SignalLabel(int rssi) => rssi switch
        {
            >= -50 => "Excellent",
            >= -60 => "Good",
            >= -70 => "Fair",
            >= -80 => "Weak",
            _ => "Very weak",
        };

        public static string ProfileSecurity(string auth, string cipher)
        {
            string a = auth.ToUpperInvariant() switch
            {
                "OPEN" => "Open",
                "SHARED" => "WEP",
                "WPA" => "WPA-Enterprise",
                "WPAPSK" => "WPA-Personal",
                "WPA2" => "WPA2-Enterprise",
                "WPA2PSK" => "WPA2-Personal",
                "WPA3" => "WPA3-Enterprise",
                "WPA3SAE" => "WPA3-Personal",
                "OWE" => "Enhanced Open",
                "" => "",
                _ => auth,
            };
            return a.Length == 0 ? "" : cipher.Length > 0 && !cipher.Equals("none", StringComparison.OrdinalIgnoreCase) && a != "Open" ? $"{a} ({cipher})" : a;
        }
    }
}
