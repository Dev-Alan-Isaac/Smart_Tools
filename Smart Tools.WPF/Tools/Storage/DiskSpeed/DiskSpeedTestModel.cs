using System.IO;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Disk Speed Test data model. Plain data - no WPF, no I/O.
    //  Speeds are decimal MB/s (1 MB = 1,000,000 bytes), the unit drive
    //  makers and CrystalDiskMark use, so numbers compare with spec sheets.
    // =====================================================================

    public enum SpeedTestPhase
    {
        Idle,
        Preparing,
        Writing,
        Reading,
        Complete,
        Cancelled,
        Failed,
    }

    /// <summary>Rough class of drive the result is typical of (by sequential read).</summary>
    public enum SpeedTier
    {
        /// <summary>Under ~200 MB/s: hard drives, USB sticks, SD cards, most network shares.</summary>
        Hdd,

        /// <summary>~200-600 MB/s: SATA SSDs, fast USB SSDs.</summary>
        SataSsd,

        /// <summary>~600-4,000 MB/s: PCIe 3 NVMe SSDs.</summary>
        Nvme,

        /// <summary>Over ~4,000 MB/s: PCIe 4/5 NVMe SSDs.</summary>
        NvmeFast,
    }

    /// <summary>Settings for one run, captured from the sidebar.</summary>
    public sealed class DiskSpeedTestOptions
    {
        /// <summary>Folder the temporary test file goes in (already checked writable by Prepare).</summary>
        public string TargetFolder { get; init; } = "";

        public long TestFileSizeBytes { get; init; } = 1_073_741_824;

        /// <summary>Bytes per read/write call. 4 MB keeps call overhead out of the number.</summary>
        public int BlockSizeBytes { get; init; } = 4 * 1024 * 1024;

        /// <summary>Bypass the Windows file cache so the numbers are the disk's, not RAM's.</summary>
        public bool BypassCache { get; init; } = true;
    }

    /// <summary>A live reading, posted about ten times a second.</summary>
    public readonly record struct SpeedSample(
        SpeedTestPhase Phase,
        double PhaseSeconds,
        double CurrentMBps,
        double AverageMBps,
        long PhaseBytes,
        long PhaseTotalBytes)
    {
        /// <summary>Progress of the whole run, 0-100 (write = first half, read = second half).</summary>
        public double OverallPercent
        {
            get
            {
                double part = PhaseTotalBytes > 0 ? Math.Clamp((double)PhaseBytes / PhaseTotalBytes, 0, 1) : 0;
                return Phase == SpeedTestPhase.Reading ? 50 + part * 50 : part * 50;
            }
        }
    }

    /// <summary>What <c>Prepare</c> found out before a run.</summary>
    public sealed class SpeedTestPreflight
    {
        /// <summary>Where the test file will actually go (may differ from what was picked - see Note).</summary>
        public string? Folder { get; init; }
        public long FreeBytes { get; init; } = -1;
        public long RequiredBytes { get; init; }

        /// <summary>Why the run can't start; null when it can.</summary>
        public string? Error { get; init; }

        /// <summary>Something the user should know, e.g. "Using C:\Users\me\AppData\Local\Temp (the drive root needs admin rights)".</summary>
        public string? Note { get; init; }

        public bool CanRun => Error == null && Folder != null;
    }

    /// <summary>One completed run. Saved to history, so keep it serializable.</summary>
    public sealed class DiskSpeedTestResult
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string TargetDisplay { get; init; } = "";
        public long TestFileSizeBytes { get; init; }
        public double WriteMBps { get; init; }
        public double ReadMBps { get; init; }
        public double WritePeakMBps { get; init; }
        public double ReadPeakMBps { get; init; }

        /// <summary>False when the drive didn't allow unbuffered I/O, so the read may be flattered by RAM cache.</summary>
        public bool CacheBypassed { get; init; } = true;

        public SpeedTier Tier { get; init; }

        [JsonIgnore] public string SizeDisplay => DiskSizeText.Format(TestFileSizeBytes);
        [JsonIgnore] public string WriteDisplay => DiskSizeText.Speed(WriteMBps);
        [JsonIgnore] public string ReadDisplay => DiskSizeText.Speed(ReadMBps);
        [JsonIgnore] public string TimestampDisplay => Timestamp.ToString("g");
        [JsonIgnore] public string TierLabel => DiskSizeText.TierLabel(Tier);

        /// <summary>0-100 on the history bars, set by the view relative to the fastest run shown.</summary>
        [JsonIgnore] public double WriteBarPercent { get; set; }
        [JsonIgnore] public double ReadBarPercent { get; set; }
    }

    /// <summary>A drive in the picker.</summary>
    public sealed class DriveTarget
    {
        public required string RootPath { get; init; }
        public required string Label { get; init; }
        public long FreeBytes { get; init; }
        public long TotalBytes { get; init; }
        public DriveType DriveType { get; init; }
        public bool IsSystemDrive { get; init; }

        public string DisplayName => $"{Label} · {DiskSizeText.Format(FreeBytes)} free";

        // A themed ComboBox's closed box shows ToString(), not DisplayMemberPath.
        public override string ToString() => DisplayName;
    }

    /// <summary>Formatting shared by the model's display properties and the manager.</summary>
    public static class DiskSizeText
    {
        public static string Format(long bytes)
        {
            if (bytes < 0) return "unknown";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
        }

        /// <summary>"38.4 MB/s", "512 MB/s", "3,412 MB/s".</summary>
        public static string Speed(double mbps) =>
            mbps <= 0 ? "-" : mbps < 100 ? $"{mbps:0.0} MB/s" : $"{mbps:N0} MB/s";

        /// <summary>The number alone, for the big tiles: "38.4", "3,412".</summary>
        public static string SpeedNumber(double mbps) =>
            mbps <= 0 ? "-" : mbps < 100 ? $"{mbps:0.0}" : $"{mbps:N0}";

        public static string TierLabel(SpeedTier tier) => tier switch
        {
            SpeedTier.Hdd => "Hard drive / USB class",
            SpeedTier.SataSsd => "SATA SSD class",
            SpeedTier.Nvme => "NVMe SSD class",
            SpeedTier.NvmeFast => "Fast NVMe (PCIe 4+) class",
            _ => "",
        };
    }
}
