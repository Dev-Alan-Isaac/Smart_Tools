using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// MAC address -> manufacturer, from the IEEE OUI registry. Reads the bundled
    /// snapshot <c>Data\oui.txt</c> (one <c>PREFIX&lt;TAB&gt;Name</c> per line, prefix = first
    /// 3 bytes as 6 hex digits) once, on first use. Plain logic, no WPF. Shared by the
    /// Network Speed Widget and the Device Scanner. A missing file just means "unknown".
    /// </summary>
    public sealed class OuiLookupManager
    {
        /// <summary>Shown instead of a manufacturer for locally administered (randomized) MACs.</summary>
        public const string PrivateName = "Private (randomized)";

        public static OuiLookupManager Shared { get; } = new();

        private readonly object _gate = new();
        private Dictionary<string, string>? _table;

        private OuiLookupManager() { }

        /// <summary>True when the table was found and has entries.</summary>
        public bool IsAvailable
        {
            get { EnsureLoaded(); return _table!.Count > 0; }
        }

        /// <summary>
        /// Manufacturer for any MAC text ("AA-BB-CC-..", "aa:bb:cc:..", "aabbcc..."),
        /// <see cref="PrivateName"/> for randomized MACs, or null when unknown.
        /// </summary>
        public string? Lookup(string? mac)
        {
            string? prefix = Prefix(mac);
            if (prefix == null) return null;
            if (IsRandomized(prefix)) return PrivateName;
            EnsureLoaded();
            return _table!.TryGetValue(prefix, out var name) ? name : null;
        }

        /// <summary>Normalizes to the first 6 hex digits (upper case), or null if too short / not hex.</summary>
        public static string? Prefix(string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return null;
            Span<char> buf = stackalloc char[6];
            int n = 0;
            foreach (char c in mac)
            {
                if (c is '-' or ':' or '.' or ' ') continue;
                if (!Uri.IsHexDigit(c)) return null;
                buf[n++] = char.ToUpperInvariant(c);
                if (n == 6) return new string(buf);
            }
            return null;
        }

        /// <summary>The "locally administered" bit (0x02 of the first byte) is set.</summary>
        public static bool IsRandomized(string prefix) =>
            prefix.Length >= 2 && (Convert.ToInt32(prefix[..2], 16) & 0x02) != 0;

        private void EnsureLoaded()
        {
            if (_table != null) return;
            lock (_gate)
            {
                if (_table != null) return;
                var table = new Dictionary<string, string>(45_000, StringComparer.Ordinal);
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "oui.txt");
                    if (File.Exists(path))
                    {
                        foreach (string line in File.ReadLines(path))
                        {
                            int tab = line.IndexOf('\t');
                            if (tab != 6) continue;
                            table[line[..6]] = line[(tab + 1)..];
                        }
                    }
                }
                catch (IOException ex) { Smart_Tools.WPF.Log.Debug("OUI table could not be read: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { Smart_Tools.WPF.Log.Debug("OUI table access denied: " + ex.Message); }
                _table = table;
            }
        }
    }
}
