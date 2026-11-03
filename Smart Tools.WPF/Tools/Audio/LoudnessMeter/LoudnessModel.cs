using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    public enum LoudnessMode { Loudness, Peak }

    public enum LoudnessStatus { Waiting, Measuring, Measured, Normalizing, Done, Silent, Failed, Cancelled }

    /// <summary>How a measured file compares to the target.</summary>
    public enum LoudnessVerdict { Unknown, OnTarget, TooLoud, TooQuiet, MayClip }

    /// <summary>A named loudness goal, e.g. "YouTube -14 LUFS".</summary>
    public sealed record LoudnessTarget(string Name, double Lufs);

    /// <summary>What one ffmpeg measuring pass found. Null = silence (ffmpeg prints -inf).</summary>
    public sealed record LoudnessMeasure(double? IntegratedLufs, double? TruePeakDb, double? RangeLu, double? ThresholdLufs, double? OffsetLu);

    public sealed class LoudnessOptions
    {
        public LoudnessMode Mode { get; set; } = LoudnessMode.Loudness;
        public double TargetLufs { get; set; } = -14;
        /// <summary>The highest true peak allowed, in dBTP. In Peak mode this is the peak the copy is raised or lowered to.</summary>
        public double CeilingDb { get; set; } = -1;
        public int BitrateKbps { get; set; } = 192;
        /// <summary>Where copies go; empty = next to each original.</summary>
        public string OutputFolder { get; set; } = "";
    }

    public sealed class LoudnessItem : INotifyPropertyChanged
    {
        private LoudnessStatus _status = LoudnessStatus.Waiting;
        private string _message = "";
        private LoudnessMeasure? _measure;
        private LoudnessVerdict _verdict;
        private double? _delta;
        private double? _achieved;
        private double _duration;

        public LoudnessItem(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            Folder = System.IO.Path.GetDirectoryName(path) ?? "";
        }

        public string Path { get; }
        public string Name { get; }
        public string Folder { get; }

        public double Duration { get => _duration; set { _duration = value; Raise(nameof(DurationText)); } }
        public string DurationText => _duration <= 0 ? "" : TimeSpan.FromSeconds(Math.Round(_duration)).ToString(_duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

        public LoudnessStatus Status { get => _status; set { if (_status != value) { _status = value; Raise(nameof(Status)); Raise(nameof(StatusText)); } } }
        public string Message { get => _message; set { if (_message != value) { _message = value; Raise(nameof(Message)); } } }

        public LoudnessMeasure? Measure
        {
            get => _measure;
            set { _measure = value; Raise(nameof(Measure)); Raise(nameof(LufsText)); Raise(nameof(PeakText)); Raise(nameof(RangeText)); }
        }

        public LoudnessVerdict Verdict { get => _verdict; set { if (_verdict != value) { _verdict = value; Raise(nameof(Verdict)); Raise(nameof(VerdictText)); } } }

        /// <summary>Measured loudness minus the target (LU): positive = louder than the target.</summary>
        public double? Delta { get => _delta; set { _delta = value; Raise(nameof(Delta)); Raise(nameof(DeltaText)); } }

        /// <summary>Loudness of the normalized copy, measured again after it was written.</summary>
        public double? Achieved { get => _achieved; set { _achieved = value; Raise(nameof(Achieved)); Raise(nameof(AchievedText)); } }

        /// <summary>The normalized copy, once written.</summary>
        public string OutputPath { get; set; } = "";

        public string LufsText => _measure == null ? "" : Fmt(_measure.IntegratedLufs, "LUFS");
        public string PeakText => _measure == null ? "" : Fmt(_measure.TruePeakDb, "dBTP");
        public string RangeText => _measure?.RangeLu is double r ? r.ToString("0.0", CultureInfo.CurrentCulture) + " LU" : "";
        public string DeltaText => _delta is double d ? (d > 0 ? "+" : "") + d.ToString("0.0", CultureInfo.CurrentCulture) + " LU" : "";
        public string AchievedText => _achieved is double a ? a.ToString("0.0", CultureInfo.CurrentCulture) + " LUFS" : "";

        public string VerdictText => _verdict switch
        {
            LoudnessVerdict.OnTarget => "On target",
            LoudnessVerdict.TooLoud => "Too loud",
            LoudnessVerdict.TooQuiet => "Too quiet",
            LoudnessVerdict.MayClip => "May clip",
            _ => "",
        };

        public string StatusText => _status switch
        {
            LoudnessStatus.Waiting => "Waiting",
            LoudnessStatus.Measuring => "Measuring",
            LoudnessStatus.Measured => "Measured",
            LoudnessStatus.Normalizing => "Writing copy",
            LoudnessStatus.Done => "Copy saved",
            LoudnessStatus.Silent => "Silent",
            LoudnessStatus.Failed => "Failed",
            _ => "Stopped",
        };

        private static string Fmt(double? v, string unit) =>
            v is double d ? d.ToString("0.0", CultureInfo.CurrentCulture) + " " + unit : "-inf";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
