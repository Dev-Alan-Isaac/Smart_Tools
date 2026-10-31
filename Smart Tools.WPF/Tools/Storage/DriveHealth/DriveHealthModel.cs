namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Drive Health data model. Plain data - no WPF, no WMI, no P/Invoke.
    // =====================================================================

    /// <summary>
    /// Physical media type from Windows' Storage API (MSFT_PhysicalDisk.MediaType).
    /// Unknown covers "Windows couldn't tell" and drives behind USB/RAID
    /// layers that hide it. Also used by scan tools (GetMediaKindForPath) -
    /// treat Unknown like an HDD (the conservative choice).
    /// </summary>
    public enum DriveKind
    {
        Unknown,
        Hdd,
        Ssd,

        /// <summary>Storage Class Memory (e.g. Optane).</summary>
        StorageClassMemory,
    }

    public enum DriveHealthStatus
    {
        Unknown,
        Healthy,
        Warning,
        Unhealthy,
    }

    /// <summary>Where the SMART data came from.</summary>
    public enum SmartSource
    {
        None,

        /// <summary>ATA SMART READ DATA / THRESHOLDS via IOCTL_ATA_PASS_THROUGH (SATA HDDs and SSDs; needs admin).</summary>
        AtaPassThrough,

        /// <summary>NVMe Health Information log page via IOCTL_STORAGE_QUERY_PROPERTY.</summary>
        NvmeHealthLog,

        /// <summary>Legacy root\WMI MSStorageDriver_FailurePredictData (old IDE-era controllers).</summary>
        LegacyWmi,
    }

    /// <summary>Why a drive has no SMART data - so the card can say something specific.</summary>
    public enum SmartUnavailableReason
    {
        None,
        NeedsAdministrator,
        UsbBridge,
        RaidOrVirtual,
        NvmeQueryFailed,
        NotReported,
    }

    /// <summary>Temperature band, for the tile colour.</summary>
    public enum TemperatureLevel
    {
        Unknown,
        Normal,
        Warm,
        Hot,
    }

    /// <summary>One row of the ATA SMART attribute table.</summary>
    public sealed class SmartAttribute
    {
        public byte Id { get; init; }
        public required string Name { get; init; }

        /// <summary>Normalized value (usually 1-253, higher is better).</summary>
        public byte Current { get; init; }
        public byte Worst { get; init; }

        /// <summary>Manufacturer failure threshold (0 = none / not read).</summary>
        public byte Threshold { get; init; }

        /// <summary>Low 6 bytes of the raw value, little-endian.</summary>
        public long Raw { get; init; }

        /// <summary>At or below the failure threshold - the drive itself says this attribute has failed.</summary>
        public bool IsFailing => Threshold > 0 && Current <= Threshold;

        /// <summary>One of the attributes that predict failure (reallocated, pending, uncorrectable...).</summary>
        public bool IsCritical { get; init; }

        /// <summary>Critical attribute with a non-zero raw count, or a failing one.</summary>
        public bool NeedsAttention => IsFailing || (IsCritical && Raw > 0);

        public string IdHex => $"{Id:X2}";
        public string ThresholdText => Threshold > 0 ? Threshold.ToString() : "-";
    }

    /// <summary>The NVMe "SMART / Health Information" log page (log ID 02h), decoded.</summary>
    public sealed class NvmeHealthLog
    {
        /// <summary>Bit 0 spare low, 1 temperature, 2 reliability degraded, 3 read-only, 4 volatile backup failed.</summary>
        public byte CriticalWarning { get; init; }
        public int TemperatureCelsius { get; init; }
        public byte AvailableSparePercent { get; init; }
        public byte AvailableSpareThresholdPercent { get; init; }

        /// <summary>Share of rated endurance used; can exceed 100.</summary>
        public byte PercentageUsed { get; init; }

        public ulong DataUnitsRead { get; init; }
        public ulong DataUnitsWritten { get; init; }
        public ulong PowerCycles { get; init; }
        public ulong PowerOnHours { get; init; }
        public ulong UnsafeShutdowns { get; init; }
        public ulong MediaErrors { get; init; }
        public ulong ErrorLogEntries { get; init; }

        /// <summary>One NVMe data unit is 1,000 512-byte blocks.</summary>
        public const long BytesPerDataUnit = 512_000;
    }

    /// <summary>SMART values the view shows, normalised across ATA and NVMe.</summary>
    public sealed class SmartData
    {
        public SmartSource Source { get; init; }
        public int? TemperatureCelsius { get; init; }
        public long? PowerOnHours { get; init; }
        public long? PowerCycles { get; init; }
        public long? ReallocatedSectors { get; init; }
        public long? PendingSectors { get; init; }
        public long? UncorrectableSectors { get; init; }
        public long? MediaErrors { get; init; }
        public long? UnsafeShutdowns { get; init; }
        public long? BytesWritten { get; init; }
        public long? BytesRead { get; init; }

        /// <summary>SSD life left, 0-100 (NVMe: 100 - percentage used; ATA: vendor wear attribute).</summary>
        public int? LifeRemainingPercent { get; init; }

        public int? AvailableSparePercent { get; init; }
        public int? AvailableSpareThresholdPercent { get; init; }
        public byte NvmeCriticalWarning { get; init; }

        public List<SmartAttribute> Attributes { get; init; } = new();
    }

    /// <summary>One plain-language line explaining the health verdict.</summary>
    public sealed class HealthFinding
    {
        public DriveHealthStatus Severity { get; init; }
        public required string Title { get; init; }
        public string Detail { get; init; } = "";
        public bool HasDetail => Detail.Length > 0;
    }

    /// <summary>One physical disk.</summary>
    public sealed class DriveHealthInfo
    {
        public required string DeviceId { get; init; }
        public int DiskNumber { get; init; }
        public required string FriendlyName { get; init; }
        public string SerialNumber { get; init; } = "";
        public string FirmwareVersion { get; init; } = "";
        public DriveKind Kind { get; init; } = DriveKind.Unknown;
        public string BusType { get; init; } = "";
        public long SizeBytes { get; init; }

        /// <summary>What Windows itself reports (MSFT_PhysicalDisk.HealthStatus).</summary>
        public DriveHealthStatus ReportedStatus { get; init; } = DriveHealthStatus.Unknown;

        /// <summary>Final verdict: Windows' status combined with the SMART findings.</summary>
        public DriveHealthStatus HealthStatus { get; init; } = DriveHealthStatus.Unknown;

        public List<string> DriveLetters { get; init; } = new();
        public SmartData? Smart { get; init; }
        public SmartUnavailableReason UnavailableReason { get; init; }
        public List<HealthFinding> Findings { get; init; } = new();

        // ---------- derived (for binding) ----------

        public bool IsNvme => BusType == "NVMe";
        public bool IsUsb => BusType == "USB";
        public bool SmartAvailable => Smart != null;

        // Old names kept so existing callers keep compiling.
        public double? TemperatureCelsius => Smart?.TemperatureCelsius;
        public long? PowerOnHours => Smart?.PowerOnHours;
        public long? ReallocatedSectors => Smart?.ReallocatedSectors;
        public long? PendingSectors => Smart?.PendingSectors;

        public string SizeDisplay => DriveHealthText.FormatBytes(SizeBytes);
        public string DriveLettersDisplay => DriveLetters.Count > 0 ? string.Join(", ", DriveLetters) : "No drive letter";

        public string KindLabel => (Kind, IsNvme) switch
        {
            (_, true) => "NVMe SSD",
            (DriveKind.Ssd, _) => IsUsb ? "USB SSD" : "SATA SSD",
            (DriveKind.Hdd, _) => IsUsb ? "USB hard drive" : "Hard drive",
            (DriveKind.StorageClassMemory, _) => "Storage class memory",
            _ => IsUsb ? "USB drive" : "Drive",
        };

        /// <summary>"NVMe SSD · 953.9 GB · C:, D:"</summary>
        public string Subtitle => $"{KindLabel} · {SizeDisplay} · {DriveLettersDisplay}";

        public string HealthText => HealthStatus switch
        {
            DriveHealthStatus.Healthy => "Healthy",
            DriveHealthStatus.Warning => "Warning",
            DriveHealthStatus.Unhealthy => "Failing",
            _ => "Unknown",
        };

        public bool HasTemperature => Smart?.TemperatureCelsius != null;
        public string TemperatureText => Smart?.TemperatureCelsius is int t ? $"{t} °C" : "";
        public TemperatureLevel TemperatureLevel => DriveHealthText.TemperatureLevelFor(Kind, IsNvme, Smart?.TemperatureCelsius);

        public bool HasPowerOn => Smart?.PowerOnHours != null;
        public string PowerOnText => Smart?.PowerOnHours is long h ? DriveHealthText.FormatHours(h) : "";
        public string PowerOnDetail => Smart?.PowerOnHours is long h ? $"{h:N0} hours powered on" : "";

        public bool HasPowerCycles => Smart?.PowerCycles != null;
        public string PowerCyclesText => Smart?.PowerCycles is long c ? $"{c:N0}" : "";

        public bool HasWritten => Smart?.BytesWritten != null;
        public string WrittenText => Smart?.BytesWritten is long b ? DriveHealthText.FormatDecimalBytes(b) : "";

        public bool HasReallocated => Smart?.ReallocatedSectors != null;
        public string ReallocatedText => Smart?.ReallocatedSectors is long r ? $"{r:N0}" : "";
        public bool ReallocatedAlert => Smart?.ReallocatedSectors > 0;

        public bool HasPending => Smart?.PendingSectors != null;
        public string PendingText => Smart?.PendingSectors is long p ? $"{p:N0}" : "";
        public bool PendingAlert => Smart?.PendingSectors > 0;

        public bool HasUncorrectable => Smart?.UncorrectableSectors != null;
        public string UncorrectableText => Smart?.UncorrectableSectors is long u ? $"{u:N0}" : "";
        public bool UncorrectableAlert => Smart?.UncorrectableSectors > 0;

        public bool HasMediaErrors => Smart?.MediaErrors != null;
        public string MediaErrorsText => Smart?.MediaErrors is long m ? $"{m:N0}" : "";
        public bool MediaErrorsAlert => Smart?.MediaErrors > 0;

        public bool HasUnsafeShutdowns => Smart?.UnsafeShutdowns != null;
        public string UnsafeShutdownsText => Smart?.UnsafeShutdowns is long s ? $"{s:N0}" : "";

        public bool HasLife => Smart?.LifeRemainingPercent != null;
        public int LifePercent => Smart?.LifeRemainingPercent ?? 0;
        public string LifeText => HasLife ? $"{LifePercent}%" : "";

        /// <summary>Good / Low (≤ 20%) / Critical (≤ 5%), for the bar colour.</summary>
        public string LifeLevel => LifePercent <= 5 ? "Critical" : LifePercent <= 20 ? "Low" : "Good";

        public bool HasAttributes => Smart?.Attributes.Count > 0;
        public List<SmartAttribute> Attributes => Smart?.Attributes ?? new();

        public string SourceText => Smart?.Source switch
        {
            SmartSource.AtaPassThrough => "ATA SMART",
            SmartSource.NvmeHealthLog => "NVMe health log",
            SmartSource.LegacyWmi => "SMART (WMI)",
            _ => "Windows status only",
        };

        /// <summary>Footer: "Serial S4EWNX0R123 · Firmware 2B2QEXM7 · NVMe health log".</summary>
        public string DetailsLine
        {
            get
            {
                var parts = new List<string>();
                if (SerialNumber.Length > 0) parts.Add($"Serial {SerialNumber}");
                if (FirmwareVersion.Length > 0) parts.Add($"Firmware {FirmwareVersion}");
                parts.Add($"Disk {DiskNumber}");
                parts.Add(SourceText);
                return string.Join(" · ", parts);
            }
        }

        /// <summary>Why there's no SMART data, in plain words (empty when there is).</summary>
        public string UnavailableText => UnavailableReason switch
        {
            SmartUnavailableReason.None => "",
            SmartUnavailableReason.NeedsAdministrator => "SMART details need administrator rights for this drive. Restart as administrator to read them.",
            SmartUnavailableReason.UsbBridge => "This USB enclosure doesn't pass SMART data through, so only Windows' own status is shown.",
            SmartUnavailableReason.RaidOrVirtual => "This is a RAID, Storage Spaces or virtual disk - SMART is only readable from the physical drives behind it.",
            SmartUnavailableReason.NvmeQueryFailed => "Windows didn't return this NVMe drive's health log (some older NVMe drivers don't support it).",
            _ => "This drive didn't report SMART data.",
        };
    }

    /// <summary>Result of one Refresh.</summary>
    public sealed class DriveHealthScan
    {
        public List<DriveHealthInfo> Drives { get; init; } = new();
        public bool IsElevated { get; init; }
        public TimeSpan Elapsed { get; init; }

        /// <summary>Set when Windows' storage information couldn't be read at all.</summary>
        public string? ErrorMessage { get; init; }

        public int HealthyCount => Drives.Count(d => d.HealthStatus == DriveHealthStatus.Healthy);
        public int WarningCount => Drives.Count(d => d.HealthStatus == DriveHealthStatus.Warning);
        public int UnhealthyCount => Drives.Count(d => d.HealthStatus == DriveHealthStatus.Unhealthy);
    }

    /// <summary>Formatting shared by the model and the manager.</summary>
    public static class DriveHealthText
    {
        /// <summary>Binary units, like Explorer ("953.9 GB" for a 1 TB drive).</summary>
        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.#} {units[unit]}";
        }

        /// <summary>Decimal units, like drive endurance specs ("42.1 TB written").</summary>
        public static string FormatDecimalBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1000 && unit < units.Length - 1)
            {
                size /= 1000;
                unit++;
            }
            return $"{size:0.#} {units[unit]}";
        }

        /// <summary>"18 h", "12 days", "1.4 years".</summary>
        public static string FormatHours(long hours)
        {
            if (hours < 48) return $"{hours} h";
            double days = hours / 24.0;
            if (days < 60) return $"{days:0} days";
            double months = days / 30.44;
            if (months < 24) return $"{months:0} months";
            return $"{days / 365.25:0.#} years";
        }

        /// <summary>HDDs are happiest below ~45 °C and worry past 55; SSDs throttle around 70.</summary>
        public static TemperatureLevel TemperatureLevelFor(DriveKind kind, bool nvme, int? celsius)
        {
            if (celsius is not int t) return TemperatureLevel.Unknown;
            bool solidState = nvme || kind is DriveKind.Ssd or DriveKind.StorageClassMemory;
            (int warm, int hot) = solidState ? (60, 70) : (45, 55);
            return t >= hot ? TemperatureLevel.Hot : t >= warm ? TemperatureLevel.Warm : TemperatureLevel.Normal;
        }
    }
}
