using Smart_Tools.WPF.Models;
using System.Buffers.Binary;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Drive Health backend. Split in three files:
    /// <list type="bullet">
    /// <item>DriveHealthManager.cs (this file) - pure logic: decoding SMART
    /// pages, the health verdict and its plain-language findings. No
    /// Windows APIs, so it's unit tested.</item>
    /// <item>DriveHealthManager.Native.cs - talking to the drives
    /// (ATA pass-through for SATA, the NVMe health log page).</item>
    /// <item>DriveHealthManager.Wmi.cs - listing disks through Windows'
    /// Storage API and putting it all together.</item>
    /// </list>
    ///
    /// Where the data comes from:
    /// 1. MSFT_PhysicalDisk (Storage API) - media type, bus, size and
    ///    Windows' own health status. No admin needed.
    /// 2. SATA drives: ATA SMART READ DATA + READ THRESHOLDS via
    ///    IOCTL_ATA_PASS_THROUGH. Needs admin.
    /// 3. NVMe drives: the NVMe Health Information log page via
    ///    IOCTL_STORAGE_QUERY_PROPERTY (the old version skipped NVMe
    ///    entirely, which is most modern system drives).
    /// 4. Legacy root\WMI MSStorageDriver_FailurePredictData - last resort.
    /// </summary>
    public static partial class DriveHealthManager
    {
        // =================================================================
        //  ATA SMART decoding
        // =================================================================

        private const int AtaTableStart = 2;
        private const int AtaEntrySize = 12;
        private const int AtaEntryCount = 30;

        public const byte AttrReallocatedSectors = 0x05;
        public const byte AttrPowerOnHours = 0x09;
        public const byte AttrPowerCycles = 0x0C;
        public const byte AttrWearLevelingCount = 0xB1;      // 177 (Samsung and others)
        public const byte AttrReportedUncorrectable = 0xBB;  // 187
        public const byte AttrAirflowTemperature = 0xBE;     // 190
        public const byte AttrTemperature = 0xC2;            // 194
        public const byte AttrReallocationEvents = 0xC4;     // 196
        public const byte AttrPendingSectors = 0xC5;         // 197
        public const byte AttrOfflineUncorrectable = 0xC6;   // 198
        public const byte AttrRemainingLife = 0xA9;          // 169
        public const byte AttrSsdLifeLeft = 0xE7;            // 231
        public const byte AttrMediaWearout = 0xE9;           // 233 (Intel)

        /// <summary>Attributes whose non-zero raw count means sectors are going bad.</summary>
        private static readonly HashSet<byte> CriticalAttributes = new()
        {
            AttrReallocatedSectors, AttrReportedUncorrectable, AttrReallocationEvents,
            AttrPendingSectors, AttrOfflineUncorrectable,
        };

        private static readonly Dictionary<byte, string> AttributeNames = new()
        {
            [0x01] = "Read error rate",
            [0x02] = "Throughput performance",
            [0x03] = "Spin-up time",
            [0x04] = "Start/stop count",
            [0x05] = "Reallocated sectors",
            [0x07] = "Seek error rate",
            [0x08] = "Seek time performance",
            [0x09] = "Power-on hours",
            [0x0A] = "Spin retry count",
            [0x0B] = "Calibration retries",
            [0x0C] = "Power cycles",
            [0x0D] = "Soft read error rate",
            [0xA9] = "Remaining life",
            [0xAA] = "Available reserved space",
            [0xAB] = "Program fail count",
            [0xAC] = "Erase fail count",
            [0xAD] = "Wear leveling",
            [0xAE] = "Unexpected power loss",
            [0xAF] = "Program fail count (chip)",
            [0xB1] = "Wear leveling count",
            [0xB3] = "Used reserved blocks",
            [0xB4] = "Unused reserved blocks",
            [0xB5] = "Program fail count",
            [0xB6] = "Erase fail count",
            [0xB7] = "SATA downshift errors",
            [0xB8] = "End-to-end errors",
            [0xBB] = "Reported uncorrectable",
            [0xBC] = "Command timeouts",
            [0xBD] = "High fly writes",
            [0xBE] = "Airflow temperature",
            [0xBF] = "G-sense error rate",
            [0xC0] = "Power-off retract count",
            [0xC1] = "Load/unload cycles",
            [0xC2] = "Temperature",
            [0xC3] = "Hardware ECC recovered",
            [0xC4] = "Reallocation events",
            [0xC5] = "Pending sectors",
            [0xC6] = "Offline uncorrectable",
            [0xC7] = "CRC errors (cable)",
            [0xC8] = "Write error rate",
            [0xCA] = "Percent lifetime used",
            [0xDC] = "Disk shift",
            [0xE1] = "Host writes",
            [0xE7] = "SSD life left",
            [0xE8] = "Available reserved space",
            [0xE9] = "Media wearout indicator",
            [0xF0] = "Head flying hours",
            [0xF1] = "Total LBAs written",
            [0xF2] = "Total LBAs read",
        };

        public static string AttributeName(byte id) => AttributeNames.TryGetValue(id, out var n) ? n : "Vendor specific";

        /// <summary>
        /// Decodes a 512-byte SMART READ DATA page: a 2-byte revision, then
        /// up to 30 twelve-byte entries (ID, 2 flag bytes, current, worst,
        /// 6-byte raw, reserved). Thresholds come from READ THRESHOLDS
        /// (same layout, byte 1 of each entry) when available.
        /// </summary>
        public static List<SmartAttribute> ParseAtaAttributes(ReadOnlySpan<byte> data, IReadOnlyDictionary<byte, byte>? thresholds = null)
        {
            var list = new List<SmartAttribute>();
            for (int i = 0; i < AtaEntryCount; i++)
            {
                int offset = AtaTableStart + i * AtaEntrySize;
                if (offset + AtaEntrySize > data.Length) break;

                byte id = data[offset];
                if (id == 0) continue;   // unused slot

                long raw = 0;
                for (int b = 5; b >= 0; b--) raw = (raw << 8) | data[offset + 5 + b];

                list.Add(new SmartAttribute
                {
                    Id = id,
                    Name = AttributeName(id),
                    Current = data[offset + 3],
                    Worst = data[offset + 4],
                    Threshold = thresholds != null && thresholds.TryGetValue(id, out byte t) ? t : (byte)0,
                    Raw = raw,
                    IsCritical = CriticalAttributes.Contains(id),
                });
            }
            return list;
        }

        /// <summary>Decodes a SMART READ THRESHOLDS page: ID and threshold per 12-byte entry.</summary>
        public static Dictionary<byte, byte> ParseAtaThresholds(ReadOnlySpan<byte> data)
        {
            var map = new Dictionary<byte, byte>();
            for (int i = 0; i < AtaEntryCount; i++)
            {
                int offset = AtaTableStart + i * AtaEntrySize;
                if (offset + AtaEntrySize > data.Length) break;
                byte id = data[offset];
                if (id != 0) map[id] = data[offset + 1];
            }
            return map;
        }

        /// <summary>Turns decoded ATA attributes into the view's SMART values.</summary>
        public static SmartData? BuildSmartFromAta(List<SmartAttribute> attributes, SmartSource source, DriveKind kind)
        {
            if (attributes.Count == 0) return null;

            SmartAttribute? Find(byte id) => attributes.FirstOrDefault(a => a.Id == id);

            // Temperature: low byte of 194, else 190 (the rest can hold min/max history).
            int? temperature = null;
            foreach (byte id in new[] { AttrTemperature, AttrAirflowTemperature })
            {
                if (Find(id) is { } a && (a.Raw & 0xFF) is > 0 and < 120)
                {
                    temperature = (int)(a.Raw & 0xFF);
                    break;
                }
            }

            // Power-on hours: low 32 bits (some drives put minutes/ms in the upper bytes).
            long? hours = Find(AttrPowerOnHours) is { } poh ? poh.Raw & 0xFFFFFFFF : null;
            if (hours > 1_000_000) hours = null;   // implausible = vendor-specific encoding

            // SSD wear: normalized value of the first wear attribute present
            // (these count down from 100). Only for SSDs - on HDDs these IDs mean other things.
            int? life = null;
            if (kind is DriveKind.Ssd or DriveKind.StorageClassMemory)
            {
                foreach (byte id in new[] { AttrSsdLifeLeft, AttrMediaWearout, AttrRemainingLife, AttrWearLevelingCount })
                {
                    if (Find(id) is { } w && w.Current is > 0 and <= 100)
                    {
                        life = w.Current;
                        break;
                    }
                }
            }

            static long? RawOf(SmartAttribute? a) => a?.Raw & 0xFFFFFFFF;

            return new SmartData
            {
                Source = source,
                TemperatureCelsius = temperature,
                PowerOnHours = hours,
                PowerCycles = RawOf(Find(AttrPowerCycles)),
                ReallocatedSectors = RawOf(Find(AttrReallocatedSectors)),
                PendingSectors = RawOf(Find(AttrPendingSectors)),
                UncorrectableSectors = RawOf(Find(AttrOfflineUncorrectable)),
                LifeRemainingPercent = life,
                Attributes = attributes,
            };
        }

        // =================================================================
        //  NVMe health log decoding
        // =================================================================

        public const int NvmeHealthLogSize = 512;

        /// <summary>
        /// Decodes the NVMe SMART / Health Information log (NVMe 1.4 section
        /// 5.14.1.2). All counters are little-endian; the 16-byte ones are
        /// read as their low 8 bytes (more than enough for any real drive).
        /// </summary>
        public static NvmeHealthLog ParseNvmeHealthLog(ReadOnlySpan<byte> log)
        {
            if (log.Length < NvmeHealthLogSize) throw new ArgumentException("An NVMe health log is 512 bytes.", nameof(log));

            static ulong U128Low(ReadOnlySpan<byte> page, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(offset, 8));

            int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log.Slice(1, 2));

            return new NvmeHealthLog
            {
                CriticalWarning = log[0],
                TemperatureCelsius = kelvin > 0 ? kelvin - 273 : 0,
                AvailableSparePercent = log[3],
                AvailableSpareThresholdPercent = log[4],
                PercentageUsed = log[5],
                DataUnitsRead = U128Low(log, 32),
                DataUnitsWritten = U128Low(log, 48),
                PowerCycles = U128Low(log, 112),
                PowerOnHours = U128Low(log, 128),
                UnsafeShutdowns = U128Low(log, 144),
                MediaErrors = U128Low(log, 160),
                ErrorLogEntries = U128Low(log, 176),
            };
        }

        public static SmartData BuildSmartFromNvme(NvmeHealthLog log)
        {
            static long Clamp(ulong v) => v > long.MaxValue ? long.MaxValue : (long)v;
            static long? Bytes(ulong units) => units > (ulong)(long.MaxValue / NvmeHealthLog.BytesPerDataUnit) ? null : (long)units * NvmeHealthLog.BytesPerDataUnit;

            return new SmartData
            {
                Source = SmartSource.NvmeHealthLog,
                TemperatureCelsius = log.TemperatureCelsius is > 0 and < 150 ? log.TemperatureCelsius : null,
                PowerOnHours = Clamp(log.PowerOnHours),
                PowerCycles = Clamp(log.PowerCycles),
                MediaErrors = Clamp(log.MediaErrors),
                UnsafeShutdowns = Clamp(log.UnsafeShutdowns),
                BytesWritten = Bytes(log.DataUnitsWritten),
                BytesRead = Bytes(log.DataUnitsRead),
                LifeRemainingPercent = Math.Max(0, 100 - log.PercentageUsed),
                AvailableSparePercent = log.AvailableSparePercent,
                AvailableSpareThresholdPercent = log.AvailableSpareThresholdPercent,
                NvmeCriticalWarning = log.CriticalWarning,
            };
        }

        // =================================================================
        //  Verdict
        // =================================================================

        /// <summary>
        /// Combines Windows' own status with what SMART shows and explains it.
        /// Windows' status tends to stay "Healthy" until a drive is nearly
        /// dead, so early signs (reallocated / pending sectors, worn-out
        /// SSD, NVMe critical warnings) raise it to Warning or Failing.
        /// </summary>
        public static (DriveHealthStatus Status, List<HealthFinding> Findings) Evaluate(
            DriveHealthStatus reported, DriveKind kind, bool nvme, SmartData? smart)
        {
            var findings = new List<HealthFinding>();

            void Add(DriveHealthStatus severity, string title, string detail = "") =>
                findings.Add(new HealthFinding { Severity = severity, Title = title, Detail = detail });

            if (reported == DriveHealthStatus.Unhealthy)
                Add(DriveHealthStatus.Unhealthy, "Windows reports this drive is failing",
                    "Back up anything important on it now and plan to replace it.");
            else if (reported == DriveHealthStatus.Warning)
                Add(DriveHealthStatus.Warning, "Windows reports a warning for this drive",
                    "Check Settings > System > Storage > Disks & volumes for details.");

            if (smart != null)
            {
                foreach (var a in smart.Attributes.Where(a => a.IsFailing))
                {
                    Add(DriveHealthStatus.Unhealthy, $"{a.Name} is past the manufacturer's failure threshold",
                        $"Value {a.Current}, threshold {a.Threshold}. The drive itself predicts failure - back up now.");
                }

                if (smart.ReallocatedSectors > 0)
                    Add(DriveHealthStatus.Warning, $"{smart.ReallocatedSectors:N0} reallocated sectors",
                        "The drive has swapped out bad areas for spares. A few can be stable; a rising number means it's wearing out - keep a backup and check again.");

                if (smart.PendingSectors > 0)
                    Add(DriveHealthStatus.Warning, $"{smart.PendingSectors:N0} sectors waiting to be reallocated",
                        "These couldn't be read reliably. Data in them may be lost - back up soon.");

                if (smart.UncorrectableSectors > 0)
                    Add(DriveHealthStatus.Warning, $"{smart.UncorrectableSectors:N0} uncorrectable sectors",
                        "Found during the drive's own surface checks. Back up soon.");

                if (smart.MediaErrors > 0)
                    Add(DriveHealthStatus.Warning, $"{smart.MediaErrors:N0} media errors",
                        "The drive hit data it couldn't recover. Keep a current backup.");

                byte cw = smart.NvmeCriticalWarning;
                if ((cw & 0x01) != 0)
                    Add(DriveHealthStatus.Unhealthy, "Spare capacity is below the safe level",
                        $"{smart.AvailableSparePercent}% spare left (threshold {smart.AvailableSpareThresholdPercent}%). Replace the drive soon.");
                if ((cw & 0x04) != 0)
                    Add(DriveHealthStatus.Unhealthy, "The drive reports degraded reliability",
                        "Too many internal errors. Back up and replace it.");
                if ((cw & 0x08) != 0)
                    Add(DriveHealthStatus.Unhealthy, "The drive has switched to read-only",
                        "It protects your data by refusing writes. Copy everything off and replace it.");
                if ((cw & 0x02) != 0)
                    Add(DriveHealthStatus.Warning, "The drive reports a temperature warning",
                        "It has been outside its safe temperature range. Check airflow or a heatsink.");
                if ((cw & 0x10) != 0)
                    Add(DriveHealthStatus.Warning, "The drive's power-loss protection has failed");

                if (smart.LifeRemainingPercent is int life)
                {
                    if (life <= 5)
                        Add(DriveHealthStatus.Warning, $"{life}% of rated life left",
                            "It has used its rated write endurance. It may keep working, but plan a replacement.");
                    else if (life <= 20)
                        Add(DriveHealthStatus.Warning, $"{life}% of rated life left",
                            "Worn but working. Keep backups current.");
                }

                var level = DriveHealthText.TemperatureLevelFor(kind, nvme, smart.TemperatureCelsius);
                if (level == TemperatureLevel.Hot)
                    Add(DriveHealthStatus.Warning, $"Running hot ({smart.TemperatureCelsius} °C)",
                        "Heat shortens drive life and slows SSDs down. Improve airflow if it stays this warm.");
            }

            DriveHealthStatus status =
                findings.Any(f => f.Severity == DriveHealthStatus.Unhealthy) ? DriveHealthStatus.Unhealthy :
                findings.Any(f => f.Severity == DriveHealthStatus.Warning) ? DriveHealthStatus.Warning :
                reported == DriveHealthStatus.Healthy || smart != null ? DriveHealthStatus.Healthy :
                DriveHealthStatus.Unknown;

            if (status == DriveHealthStatus.Healthy)
            {
                findings.Add(new HealthFinding
                {
                    Severity = DriveHealthStatus.Healthy,
                    Title = smart != null ? "No problems found" : "Windows reports no problems",
                    Detail = smart != null ? "SMART shows no bad sectors or errors." : "SMART details weren't available, so this is Windows' own check only.",
                });
            }

            return (status, findings);
        }

        /// <summary>Why SMART is missing for a drive, from what we know about it.</summary>
        public static SmartUnavailableReason ReasonForNoSmart(string busType, bool elevated)
        {
            return busType switch
            {
                "USB" => SmartUnavailableReason.UsbBridge,
                "RAID" or "Storage Spaces" or "File-Backed Virtual" or "iSCSI" or "Virtual" => SmartUnavailableReason.RaidOrVirtual,
                "NVMe" => SmartUnavailableReason.NvmeQueryFailed,
                _ => elevated ? SmartUnavailableReason.NotReported : SmartUnavailableReason.NeedsAdministrator,
            };
        }

        // =================================================================
        //  Storage API value mapping
        // =================================================================

        public static DriveKind MapMediaType(object? value) => ToUInt16(value) switch
        {
            3 => DriveKind.Hdd,
            4 => DriveKind.Ssd,
            5 => DriveKind.StorageClassMemory,
            _ => DriveKind.Unknown,
        };

        /// <summary>MSFT_PhysicalDisk.HealthStatus: 0 Healthy, 1 Warning, 2 Unhealthy, 5 Unknown.</summary>
        public static DriveHealthStatus MapHealthStatus(object? value) => ToUInt16(value) switch
        {
            0 => DriveHealthStatus.Healthy,
            1 => DriveHealthStatus.Warning,
            2 => DriveHealthStatus.Unhealthy,
            _ => DriveHealthStatus.Unknown,
        };

        public static string MapBusType(object? value) => ToUInt16(value) switch
        {
            1 => "SCSI",
            2 => "ATAPI",
            3 => "ATA",
            4 => "IEEE 1394",
            5 => "SSA",
            6 => "Fibre Channel",
            7 => "USB",
            8 => "RAID",
            9 => "iSCSI",
            10 => "SAS",
            11 => "SATA",
            12 => "SD",
            13 => "MMC",
            14 => "Virtual",
            15 => "File-Backed Virtual",
            16 => "Storage Spaces",
            17 => "NVMe",
            18 => "SCM",
            19 => "UFS",
            _ => "Unknown",
        };

        private static int ToUInt16(object? value)
        {
            if (value == null) return -1;
            try
            {
                return Convert.ToUInt16(value);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return -1;
            }
        }
    }
}
