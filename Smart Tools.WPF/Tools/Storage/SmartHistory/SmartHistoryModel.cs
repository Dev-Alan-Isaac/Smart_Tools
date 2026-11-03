using Smart_Tools.WPF.Controllers;
using System.Globalization;

namespace Smart_Tools.WPF.Models
{
    /// <summary>Which SMART value the chart and the warnings look at.</summary>
    public enum SmartMetric
    {
        Temperature, PowerOnHours, PowerCycles, Reallocated, Pending, Uncorrectable,
        MediaErrors, UnsafeShutdowns, BytesWritten, LifeRemaining, Spare,
    }

    /// <summary>One look at a drive's SMART numbers. Saved as is; a value the drive doesn't report stays null.</summary>
    public sealed class SmartReading
    {
        public DateTime When { get; set; }
        public int? Temperature { get; set; }
        public long? PowerOnHours { get; set; }
        public long? PowerCycles { get; set; }
        public long? Reallocated { get; set; }
        public long? Pending { get; set; }
        public long? Uncorrectable { get; set; }
        public long? MediaErrors { get; set; }
        public long? UnsafeShutdowns { get; set; }
        public long? BytesWritten { get; set; }
        public int? LifeRemaining { get; set; }
        public int? Spare { get; set; }

        public double? Get(SmartMetric m) => m switch
        {
            SmartMetric.Temperature => Temperature,
            SmartMetric.PowerOnHours => PowerOnHours,
            SmartMetric.PowerCycles => PowerCycles,
            SmartMetric.Reallocated => Reallocated,
            SmartMetric.Pending => Pending,
            SmartMetric.Uncorrectable => Uncorrectable,
            SmartMetric.MediaErrors => MediaErrors,
            SmartMetric.UnsafeShutdowns => UnsafeShutdowns,
            SmartMetric.BytesWritten => BytesWritten,
            SmartMetric.LifeRemaining => LifeRemaining,
            SmartMetric.Spare => Spare,
            _ => null,
        };
    }

    /// <summary>Everything recorded for one physical drive.</summary>
    public sealed class SmartDriveHistory
    {
        /// <summary>Serial number when the drive has one, else its Windows device id - what ties readings to a drive.</summary>
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsNvme { get; set; }
        public bool IsSsd { get; set; }
        public List<SmartReading> Readings { get; set; } = new();

        public override string ToString() => Name;
    }

    public sealed class SmartHistoryFile
    {
        public int Version { get; set; } = 1;
        public List<SmartDriveHistory> Drives { get; set; } = new();
    }

    public enum SmartWarningLevel { Info, Warning, Danger }

    public sealed record SmartWarning(SmartWarningLevel Level, string Text);

    /// <summary>One grid row: a reading with the change since the one before.</summary>
    public sealed class SmartReadingRow
    {
        public SmartReadingRow(SmartReading reading, SmartReading? previous)
        {
            Reading = reading;
            When = reading.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            Temperature = reading.Temperature is int t ? $"{t} °C" : "";
            PowerOn = reading.PowerOnHours is long h ? $"{h:N0} h" : "";
            Reallocated = reading.Reallocated?.ToString("N0") ?? "";
            Pending = reading.Pending?.ToString("N0") ?? "";
            Written = reading.BytesWritten is long b ? SmartHistoryText.FormatBytes(b) : "";
            Life = reading.LifeRemaining is int l ? $"{l}%" : "";
            Change = SmartHistoryManager.DescribeChange(reading, previous);
        }

        public SmartReading Reading { get; }
        public string When { get; }
        public string Temperature { get; }
        public string PowerOn { get; }
        public string Reallocated { get; }
        public string Pending { get; }
        public string Written { get; }
        public string Life { get; }
        public string Change { get; }
        public bool Worsened => Change.Length > 0 && Change.Contains('▲');
    }

    public static class SmartHistoryText
    {
        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = bytes;
            int u = 0;
            while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
            return u == 0 ? $"{bytes:N0} B" : $"{v:0.#} {units[u]}";
        }

        public static string Label(SmartMetric m) => m switch
        {
            SmartMetric.Temperature => "Temperature",
            SmartMetric.PowerOnHours => "Power-on hours",
            SmartMetric.PowerCycles => "Power cycles",
            SmartMetric.Reallocated => "Reallocated sectors",
            SmartMetric.Pending => "Pending sectors",
            SmartMetric.Uncorrectable => "Uncorrectable sectors",
            SmartMetric.MediaErrors => "Media errors",
            SmartMetric.UnsafeShutdowns => "Unsafe shutdowns",
            SmartMetric.BytesWritten => "Data written",
            SmartMetric.LifeRemaining => "Life remaining",
            _ => "Spare capacity",
        };

        public static string Unit(SmartMetric m) => m switch
        {
            SmartMetric.Temperature => "°C",
            SmartMetric.PowerOnHours => "h",
            SmartMetric.LifeRemaining or SmartMetric.Spare => "%",
            _ => "",
        };

        public static string Format(SmartMetric m, double v) => m == SmartMetric.BytesWritten
            ? FormatBytes((long)v)
            : v.ToString("#,0.#", CultureInfo.CurrentCulture) + (Unit(m).Length > 0 ? " " + Unit(m) : "");
    }
}
