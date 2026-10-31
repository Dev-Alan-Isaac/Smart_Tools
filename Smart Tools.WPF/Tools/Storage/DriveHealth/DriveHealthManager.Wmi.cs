using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Listing disks through WMI and putting a scan together (Windows only).</summary>
    public static partial class DriveHealthManager
    {
        private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

        private static readonly object MediaKindLock = new();
        private static Dictionary<string, DriveKind>? _mediaKindByLetter;

        // =================================================================
        //  Full scan (Drive Health view)
        // =================================================================

        public static Task<DriveHealthScan> GetAllDrivesAsync(CancellationToken token = default) =>
            Task.Run(() => Scan(token), token);

        private static DriveHealthScan Scan(CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            bool elevated = IsRunningAsAdministrator();

            List<DiskRecord> disks;
            try
            {
                disks = ReadPhysicalDisks(token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Storage API missing or blocked by policy: fall back to the
                // classic Win32_DiskDrive list (no media type or health status).
                Log.Info($"DriveHealthManager: MSFT_PhysicalDisk failed: {ex.Message}");
                try
                {
                    disks = ReadWin32DiskDrives(token);
                }
                catch (Exception ex2) when (ex2 is not OperationCanceledException)
                {
                    return new DriveHealthScan
                    {
                        IsElevated = elevated,
                        Elapsed = watch.Elapsed,
                        ErrorMessage = $"Windows didn't return any drive information ({ex2.Message}).",
                    };
                }
            }

            var letters = ReadDriveLetters(disks.Select(d => d.Number), token);
            Dictionary<int, string>? pnpByIndex = null;   // only needed for the legacy SMART fallback

            var drives = new List<DriveHealthInfo>();
            foreach (var disk in disks.OrderBy(d => d.Number))
            {
                token.ThrowIfCancellationRequested();

                SmartData? smart = ReadSmart(disk, ref pnpByIndex);
                var (status, findings) = Evaluate(disk.Health, disk.Kind, disk.Bus == "NVMe", smart);

                drives.Add(new DriveHealthInfo
                {
                    DeviceId = $@"\\.\PHYSICALDRIVE{disk.Number}",
                    DiskNumber = disk.Number,
                    FriendlyName = disk.Name,
                    SerialNumber = disk.Serial,
                    FirmwareVersion = disk.Firmware,
                    Kind = disk.Kind,
                    BusType = disk.Bus,
                    SizeBytes = disk.Size,
                    ReportedStatus = disk.Health,
                    HealthStatus = status,
                    DriveLetters = letters.TryGetValue(disk.Number, out var l) ? l : new List<string>(),
                    Smart = smart,
                    UnavailableReason = smart == null ? ReasonForNoSmart(disk.Bus, elevated) : SmartUnavailableReason.None,
                    Findings = findings,
                });
            }

            // Fresh data - refresh what scan tools see through GetMediaKindForPath too.
            lock (MediaKindLock) _mediaKindByLetter = BuildMediaKindMap(drives);

            return new DriveHealthScan { Drives = drives, IsElevated = elevated, Elapsed = watch.Elapsed };
        }

        private static SmartData? ReadSmart(DiskRecord disk, ref Dictionary<int, string>? pnpByIndex)
        {
            try
            {
                if (disk.Bus == "NVMe")
                {
                    var log = TryReadNvmeHealth(disk.Number);
                    if (log != null) return BuildSmartFromNvme(log);
                }
                else if (disk.Bus is not ("USB" or "RAID" or "Storage Spaces" or "Virtual" or "File-Backed Virtual" or "iSCSI"))
                {
                    // ATA pass-through on a USB/RAID bridge just times out - skip those.
                    var attributes = TryReadAtaSmart(disk.Number);
                    if (attributes != null) return BuildSmartFromAta(attributes, SmartSource.AtaPassThrough, disk.Kind);
                }

                // Last resort for old IDE-era controllers.
                pnpByIndex ??= ReadPnpIds();
                if (pnpByIndex.TryGetValue(disk.Number, out string? pnp) && pnp.Length > 0)
                {
                    var legacy = TryReadLegacyWmiSmart(pnp);
                    if (legacy != null) return BuildSmartFromAta(legacy, SmartSource.LegacyWmi, disk.Kind);
                }
            }
            catch (Exception ex)
            {
                Log.Info($"DriveHealthManager: SMART read failed for disk {disk.Number}: {ex.Message}");
            }
            return null;
        }

        // =================================================================
        //  WMI queries
        // =================================================================

        private sealed record DiskRecord(int Number, string Name, string Serial, string Firmware, DriveKind Kind, string Bus, long Size, DriveHealthStatus Health);

        private static List<DiskRecord> ReadPhysicalDisks(CancellationToken token)
        {
            var list = new List<DiskRecord>();
            using var searcher = new ManagementObjectSearcher(StorageNamespace,
                "SELECT DeviceId, FriendlyName, SerialNumber, FirmwareVersion, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    token.ThrowIfCancellationRequested();

                    // Pool members of Storage Spaces can have non-numeric ids - they have no PhysicalDriveN.
                    if (!int.TryParse(mo["DeviceId"]?.ToString(), out int number)) continue;

                    list.Add(new DiskRecord(
                        number,
                        Clean(mo["FriendlyName"]) is { Length: > 0 } name ? name : "Unknown drive",
                        Clean(mo["SerialNumber"]),
                        Clean(mo["FirmwareVersion"]),
                        MapMediaType(mo["MediaType"]),
                        MapBusType(mo["BusType"]),
                        ToInt64(mo["Size"]),
                        MapHealthStatus(mo["HealthStatus"])));
                }
            }
            return list;
        }

        private static List<DiskRecord> ReadWin32DiskDrives(CancellationToken token)
        {
            var list = new List<DiskRecord>();
            using var searcher = new ManagementObjectSearcher(@"root\cimv2",
                "SELECT Index, Model, SerialNumber, FirmwareRevision, InterfaceType, Size, Status FROM Win32_DiskDrive");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    token.ThrowIfCancellationRequested();
                    if (mo["Index"] == null) continue;

                    string status = Clean(mo["Status"]);
                    var health = status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? DriveHealthStatus.Healthy
                        : status.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase) ? DriveHealthStatus.Unhealthy
                        : DriveHealthStatus.Unknown;

                    string bus = Clean(mo["InterfaceType"]) switch
                    {
                        "USB" => "USB",
                        "IDE" => "ATA",
                        "SCSI" => "SCSI",   // NVMe and SATA-behind-AHCI often report SCSI here
                        var other => other.Length > 0 ? other : "Unknown",
                    };

                    list.Add(new DiskRecord(Convert.ToInt32(mo["Index"]), Clean(mo["Model"]), Clean(mo["SerialNumber"]),
                        Clean(mo["FirmwareRevision"]), DriveKind.Unknown, bus, ToInt64(mo["Size"]), health));
                }
            }
            return list;
        }

        /// <summary>
        /// Drive letters per disk number in ONE query (MSFT_Partition). The
        /// old code walked Win32_DiskDrive -> partition -> logical disk
        /// associations per disk, which took seconds on some PCs; that walk
        /// is now only the fallback.
        /// </summary>
        private static Dictionary<int, List<string>> ReadDriveLetters(IEnumerable<int> diskNumbers, CancellationToken token)
        {
            var map = new Dictionary<int, List<string>>();
            try
            {
                using var searcher = new ManagementObjectSearcher(StorageNamespace, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        token.ThrowIfCancellationRequested();
                        char letter = mo["DriveLetter"] switch
                        {
                            char c => c,
                            ushort u => (char)u,
                            _ => '\0',
                        };
                        if (!char.IsLetter(letter) || mo["DiskNumber"] == null) continue;

                        int disk = Convert.ToInt32(mo["DiskNumber"]);
                        if (!map.TryGetValue(disk, out var list)) map[disk] = list = new List<string>();
                        list.Add($"{char.ToUpperInvariant(letter)}:");
                    }
                }
                foreach (var list in map.Values) list.Sort(StringComparer.OrdinalIgnoreCase);
                return map;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Info($"DriveHealthManager: MSFT_Partition failed, using associations: {ex.Message}");
            }

            foreach (int disk in diskNumbers)
            {
                token.ThrowIfCancellationRequested();
                map[disk] = ReadDriveLettersByAssociation(disk);
            }
            return map;
        }

        private static List<string> ReadDriveLettersByAssociation(int diskNumber)
        {
            var letters = new List<string>();
            try
            {
                string diskQuery = $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE{diskNumber}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition";
                using var partitions = new ManagementObjectSearcher(diskQuery);
                foreach (ManagementObject partition in partitions.Get())
                {
                    using (partition)
                    {
                        string id = partition["DeviceID"]?.ToString() ?? "";
                        if (id.Length == 0) continue;

                        using var logicals = new ManagementObjectSearcher(
                            $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{id}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                        foreach (ManagementObject logical in logicals.Get())
                        {
                            using (logical)
                            {
                                string letter = logical["DeviceID"]?.ToString() ?? "";
                                if (letter.Length > 0) letters.Add(letter);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Dynamic disks, Storage Spaces etc. - no letters is a normal outcome.
                Log.Info($"DriveHealthManager: letters for disk {diskNumber}: {ex.Message}");
            }
            letters.Sort(StringComparer.OrdinalIgnoreCase);
            return letters;
        }

        private static Dictionary<int, string> ReadPnpIds()
        {
            var map = new Dictionary<int, string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Index, PNPDeviceID FROM Win32_DiskDrive");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (mo["Index"] == null) continue;
                        map[Convert.ToInt32(mo["Index"])] = mo["PNPDeviceID"]?.ToString() ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"DriveHealthManager: Win32_DiskDrive failed: {ex.Message}");
            }
            return map;
        }

        /// <summary>
        /// Legacy SMART: InstanceName is driver-supplied, so matching it to
        /// the PNP id is a heuristic. Rarely works on modern AHCI hardware.
        /// </summary>
        private static List<SmartAttribute>? TryReadLegacyWmiSmart(string pnpDeviceId)
        {
            try
            {
                string escaped = pnpDeviceId.Replace("\\", "\\\\").Replace("'", "\\'");
                using var searcher = new ManagementObjectSearcher(@"root\WMI",
                    $"SELECT VendorSpecific, InstanceName FROM MSStorageDriver_FailurePredictData WHERE InstanceName LIKE '%{escaped}%'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (mo["VendorSpecific"] is not byte[] bytes || bytes.Length < 362) continue;
                        var attributes = ParseAtaAttributes(bytes);
                        if (attributes.Count > 0) return attributes;
                    }
                }
            }
            catch (Exception ex)
            {
                // Access denied / class missing / no instance - all normal.
                Log.Info($"DriveHealthManager: legacy SMART: {ex.Message}");
            }
            return null;
        }

        private static string Clean(object? value) => value?.ToString()?.Trim() ?? "";

        private static long ToInt64(object? value)
        {
            try
            {
                return value == null ? 0 : Convert.ToInt64(value);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return 0;
            }
        }

        // =================================================================
        //  Media kind for other tools (Similar Files etc.)
        // =================================================================

        /// <summary>
        /// Best-effort HDD/SSD lookup for a path, so scan tools can pick how
        /// much parallel I/O a drive can take. Treat Unknown like an HDD.
        /// The whole letter-to-kind map is built once (no SMART reads) and
        /// cached - the old version ran the full health scan, SMART reads
        /// included, the first time each drive letter was asked about.
        /// </summary>
        public static DriveKind GetMediaKindForPath(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return DriveKind.Unknown;
                string letter = root.TrimEnd('\\', '/').ToUpperInvariant();

                Dictionary<string, DriveKind> map;
                lock (MediaKindLock)
                {
                    map = _mediaKindByLetter ??= BuildMediaKindMapQuick();
                }
                return map.TryGetValue(letter, out var kind) ? kind : DriveKind.Unknown;
            }
            catch (Exception)
            {
                return DriveKind.Unknown;
            }
        }

        /// <summary>Forget the cached map (a drive was plugged in or removed).</summary>
        public static void InvalidateCache()
        {
            lock (MediaKindLock) _mediaKindByLetter = null;
        }

        private static Dictionary<string, DriveKind> BuildMediaKindMapQuick()
        {
            try
            {
                var disks = ReadPhysicalDisks(CancellationToken.None);
                var letters = ReadDriveLetters(disks.Select(d => d.Number), CancellationToken.None);
                var map = new Dictionary<string, DriveKind>(StringComparer.OrdinalIgnoreCase);
                foreach (var disk in disks)
                {
                    if (!letters.TryGetValue(disk.Number, out var list)) continue;
                    foreach (string l in list) map[l] = disk.Kind;
                }
                return map;
            }
            catch (Exception ex)
            {
                Log.Info($"DriveHealthManager: media kind map failed: {ex.Message}");
                return new Dictionary<string, DriveKind>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static Dictionary<string, DriveKind> BuildMediaKindMap(IEnumerable<DriveHealthInfo> drives)
        {
            var map = new Dictionary<string, DriveKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in drives)
                foreach (string l in d.DriveLetters) map[l] = d.Kind;
            return map;
        }
    }
}
