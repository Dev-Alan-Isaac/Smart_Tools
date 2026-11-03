using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Smart_Tools.WPF.Controllers
{
    // =====================================================================
    //  Wi-Fi Analyzer: lists the networks the PC hears, rates the channels
    //  and shows the profiles saved in Windows. Everything is read-only:
    //  nothing is connected, changed or sent.
    // =====================================================================
    public static class WifiManager
    {
        // =================================================================
        //  Scanning
        // =================================================================

        /// <summary>Looks for networks. <paramref name="wait"/> is how long the adapter gets to hear them after the scan request.</summary>
        public static Task<WifiScan> ScanAsync(TimeSpan wait, CancellationToken token) =>
            Task.Run(async () =>
            {
                using var session = WlanNative.Session.Open(out int openError);
                if (session == null)
                    return new WifiScan { Problem = openError == WlanNative.ErrorServiceNotActive
                        ? "The Windows Wi-Fi service (WLAN AutoConfig) isn't running, or this PC has no Wi-Fi adapter."
                        : $"Windows' Wi-Fi service couldn't be opened (error {openError})." };

                var adapters = session.Adapters();
                if (adapters.Count == 0)
                    return new WifiScan { Problem = "No Wi-Fi adapter found. Plug one in or turn Wi-Fi on." };

                // The adapter that is connected, else the first.
                var adapter = adapters.FirstOrDefault(a => session.CurrentConnection(a.Id) != null) ?? adapters[0];
                session.Scan(adapter.Id);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var bss = session.BssList(adapter.Id, out uint error);
                var available = session.AvailableList(adapter.Id);
                var conn = session.CurrentConnection(adapter.Id);

                var networks = new List<WifiNetwork>();
                var oui = OuiLookupManager.Shared;
                foreach (var b in bss)
                {
                    int channel = ChannelFromKhz(b.FrequencyKhz);
                    var av = b.Ssid.Length == 0 ? null : available.FirstOrDefault(a => a.Ssid == b.Ssid);
                    networks.Add(new WifiNetwork
                    {
                        Ssid = b.Ssid,
                        Bssid = b.Bssid,
                        Rssi = b.Rssi,
                        Quality = b.Quality,
                        FrequencyMhz = b.FrequencyKhz / 1000,
                        Channel = channel,
                        Band = BandOf(b.FrequencyKhz),
                        Standard = PhyName(b.PhyType),
                        Security = av == null ? "" : SecurityName(av.Secured, av.Auth, av.Cipher),
                        Vendor = oui.IsAvailable ? oui.Lookup(b.Bssid) ?? "" : "",
                        IsConnected = conn != null && string.Equals(conn.Bssid, b.Bssid, StringComparison.OrdinalIgnoreCase),
                    });
                }
                networks = networks.OrderByDescending(n => n.IsConnected).ThenByDescending(n => n.Rssi).ToList();

                string? problem = null;
                if (error == WlanNative.ErrorAccessDenied)
                    problem = "Windows didn't allow reading the networks. Turn on Location access (Settings > Privacy & security > Location) for desktop apps.";
                else if (networks.Count == 0)
                    problem = adapter.State == 0 || error != 0
                        ? "The adapter returned nothing. Make sure Wi-Fi is on and Location access is allowed for desktop apps."
                        : "No networks heard. Make sure Wi-Fi is on and Location access is allowed for desktop apps, then scan again.";

                return new WifiScan
                {
                    Networks = networks,
                    AdapterCount = adapters.Count,
                    AdapterName = adapter.Description,
                    Link = conn == null ? null : new WifiLink(conn.Ssid, conn.Bssid, PhyName(conn.PhyType), conn.Quality, conn.RxKbps, conn.TxKbps, conn.ProfileName),
                    Problem = problem,
                };
            }, token);

        // =================================================================
        //  Radio maths
        // =================================================================

        public static WifiBand BandOf(int frequencyKhz) => frequencyKhz switch
        {
            >= 2_400_000 and < 2_500_000 => WifiBand.Ghz24,
            >= 4_900_000 and < 5_900_000 => WifiBand.Ghz5,
            >= 5_925_000 and <= 7_125_000 => WifiBand.Ghz6,
            _ => WifiBand.Unknown,
        };

        /// <summary>The channel number of a centre frequency; 0 when it doesn't fit a known band.</summary>
        public static int ChannelFromKhz(int khz)
        {
            double mhz = khz / 1000.0;
            return BandOf(khz) switch
            {
                WifiBand.Ghz24 => Math.Abs(mhz - 2484) < 1 ? 14 : (int)Math.Round((mhz - 2407) / 5),
                WifiBand.Ghz5 => (int)Math.Round((mhz - 5000) / 5),
                WifiBand.Ghz6 => (int)Math.Round((mhz - 5950) / 5),
                _ => 0,
            };
        }

        public static string PhyName(int phy) => phy switch
        {
            4 => "802.11a",
            5 => "802.11b",
            6 => "802.11g",
            7 => "Wi-Fi 4 (n)",
            8 => "Wi-Fi 5 (ac)",
            9 => "WiGig (ad)",
            10 => "Wi-Fi 6 (ax)",
            11 => "Wi-Fi 7 (be)",
            _ => "",
        };

        public static string SecurityName(bool secured, int auth, int cipher)
        {
            if (!secured) return "Open";
            return auth switch
            {
                1 => "Open",
                2 => "WEP",
                3 => "WPA-Enterprise",
                4 => "WPA-Personal",
                6 => "WPA2-Enterprise",
                7 => "WPA2-Personal",
                8 => "WPA3-Enterprise",
                9 => "WPA3-Personal",
                10 => "Enhanced Open",
                11 => "WPA3-Enterprise",
                _ => "Secured",
            };
        }

        // =================================================================
        //  Channel advice
        // =================================================================

        /// <summary>
        /// How crowded each candidate channel is, best first. A network on or beside a channel counts more the
        /// stronger it is; on 2.4 GHz only channels 1, 6 and 11 are candidates (the rest overlap each other).
        /// </summary>
        public static List<ChannelAdvice> Advise(IEnumerable<WifiNetwork> networks, WifiBand band)
        {
            var heard = networks.Where(n => n.Band == band && n.Channel > 0).ToList();
            IEnumerable<int> candidates = band switch
            {
                WifiBand.Ghz24 => new[] { 1, 6, 11 },
                WifiBand.Ghz5 => new[] { 36, 40, 44, 48, 149, 153, 157, 161, 165 },
                WifiBand.Ghz6 => Enumerable.Range(0, 59).Select(i => 1 + i * 4),
                _ => Array.Empty<int>(),
            };

            var advice = new List<ChannelAdvice>();
            foreach (int c in candidates)
            {
                double score = 0;
                int on = 0;
                foreach (var n in heard)
                {
                    int d = Math.Abs(n.Channel - c);
                    double overlap = band == WifiBand.Ghz24 ? (d >= 5 ? 0 : (5 - d) / 5.0) : d == 0 ? 1 : d <= 2 ? 0.4 : 0;
                    if (overlap <= 0) continue;
                    double strength = Math.Pow(Math.Clamp((n.Rssi + 100) / 70.0, 0, 1), 2);
                    score += overlap * strength;
                    if (d == 0) on++;
                }
                string reason = score < 0.05 ? "nobody else is using it" : on > 0 ? $"{on} network{(on == 1 ? "" : "s")} on it" : "neighbouring channels are busy";
                advice.Add(new ChannelAdvice(band, c, score, on, reason));
            }
            return advice.OrderBy(a => a.Score).ThenBy(a => a.Channel).ToList();
        }

        // =================================================================
        //  Saved networks
        // =================================================================

        public static Task<List<WifiProfile>> ProfilesAsync(CancellationToken token) =>
            Task.Run(() =>
            {
                var list = new List<WifiProfile>();
                using var session = WlanNative.Session.Open(out _);
                if (session == null) return list;
                foreach (var adapter in session.Adapters())
                {
                    foreach (string name in session.ProfileNames(adapter.Id))
                    {
                        token.ThrowIfCancellationRequested();
                        if (list.Any(p => p.Name == name)) continue;
                        string? xml = session.ProfileXml(adapter.Id, name, withKey: false, out _);
                        list.Add(ParseProfile(name, xml));
                    }
                }
                return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }, token);

        public static WifiProfile ParseProfile(string name, string? xml)
        {
            string auth = "", enc = "";
            bool auto = false;
            try
            {
                if (!string.IsNullOrEmpty(xml))
                {
                    var doc = XDocument.Parse(xml);
                    string? Value(string local) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
                    auth = Value("authentication") ?? "";
                    enc = Value("encryption") ?? "";
                    auto = string.Equals(Value("connectionMode"), "auto", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (System.Xml.XmlException)
            {
                // a profile we can't read is still listed by name
            }
            return new WifiProfile { Name = name, Authentication = auth, Encryption = enc, AutoConnect = auto };
        }

        /// <summary>The saved password of a profile. Null password with a sentence when Windows doesn't allow it (it needs an administrator for some profiles).</summary>
        public static Task<(string? Password, string? Problem)> RevealPasswordAsync(string profileName, CancellationToken token) =>
            Task.Run<(string?, string?)>(() =>
            {
                using var session = WlanNative.Session.Open(out _);
                if (session == null) return (null, "Windows' Wi-Fi service isn't available.");
                foreach (var adapter in session.Adapters())
                {
                    token.ThrowIfCancellationRequested();
                    string? xml = session.ProfileXml(adapter.Id, profileName, withKey: true, out uint error);
                    if (xml == null)
                    {
                        if (error == WlanNative.ErrorAccessDenied) return (null, "Windows only shows this password to an administrator. Run Smart Tools as administrator.");
                        continue;
                    }
                    try
                    {
                        var key = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "keyMaterial")?.Value;
                        return string.IsNullOrEmpty(key) ? (null, "This network has no saved password (it is open or uses a certificate).") : (key, null);
                    }
                    catch (System.Xml.XmlException)
                    {
                        return (null, "The saved profile couldn't be read.");
                    }
                }
                return (null, "The profile wasn't found.");
            }, token);

        // =================================================================
        //  Report
        // =================================================================

        public static string BuildCsv(IEnumerable<WifiNetwork> networks)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Network,BSSID,Vendor,Signal dBm,Quality,Channel,Band,Standard,Security,Connected");
            foreach (var n in networks)
            {
                sb.AppendJoin(',', Csv(n.DisplayName), n.Bssid, Csv(n.Vendor), n.Rssi.ToString(CultureInfo.InvariantCulture), n.QualityText,
                    n.Channel.ToString(CultureInfo.InvariantCulture), n.BandText, Csv(n.Standard), Csv(n.Security), n.IsConnected ? "yes" : "").AppendLine();
            }
            return sb.ToString();
        }

        private static string Csv(string value)
        {
            if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
