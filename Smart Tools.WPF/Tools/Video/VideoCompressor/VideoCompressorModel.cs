using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Video Compressor & Trimmer data. Plain data - no WPF, no I/O.
    //  VideoCompressorManager fills these; the view binds.
    // =====================================================================

    public enum VcMode
    {
        /// <summary>Pick a quality; the size follows.</summary>
        Quality,

        /// <summary>Pick a size; the quality follows (two passes, so the size is close).</summary>
        TargetSize,

        /// <summary>Only cut the video: no re-encoding, so it is quick and loses nothing, but cuts land on keyframes.</summary>
        TrimOnly,
    }

    public enum VcQuality { Small, Balanced, High }

    public enum VcAudio { Keep, Smaller, Remove }

    public enum VcItemStatus { Reading, Ready, Unreadable, Working, Done, Failed, Cancelled }

    /// <summary>What ffprobe says about a video. <see cref="Error"/> is set when it can't be read.</summary>
    public sealed record VcProbe(double? Duration, int Width, int Height, double Fps, bool HasAudio, string? Error);

    /// <summary>One video in the list.</summary>
    public sealed class VcFileItem : INotifyPropertyChanged
    {
        private VcItemStatus _status = VcItemStatus.Reading;
        private double _progress;
        private double? _duration, _trimStart, _trimEnd;
        private string _message = "", _resultText = "";
        private bool _locked;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string FullPath { get; init; } = "";
        public long SizeBytes { get; init; }
        public string Name => System.IO.Path.GetFileName(FullPath);
        public string Folder => System.IO.Path.GetDirectoryName(FullPath) ?? "";

        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public bool HasAudio { get; set; } = true;

        public VcItemStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                Raise(); Raise(nameof(StatusText)); Raise(nameof(IsWorking)); Raise(nameof(CanRun));
            }
        }

        /// <summary>0..100 while working.</summary>
        public double Progress { get => _progress; set => Set(ref _progress, value); }

        public double? Duration
        {
            get => _duration;
            set { if (Set(ref _duration, value)) { Raise(nameof(DurationText)); Raise(nameof(TrimText)); } }
        }

        /// <summary>Where the kept part starts, in seconds. Null = the beginning.</summary>
        public double? TrimStart
        {
            get => _trimStart;
            set { if (Set(ref _trimStart, value)) Raise(nameof(TrimText)); }
        }

        /// <summary>Where the kept part ends, in seconds. Null = the end.</summary>
        public double? TrimEnd
        {
            get => _trimEnd;
            set { if (Set(ref _trimEnd, value)) Raise(nameof(TrimText)); }
        }

        /// <summary>The tooltip: why a video can't be read, or what happened.</summary>
        public string Message { get => _message; set => Set(ref _message, value); }

        /// <summary>The estimated size before a run, the real size after.</summary>
        public string ResultText { get => _resultText; set => Set(ref _resultText, value); }

        public string OutputPath { get; set; } = "";
        public long OutputBytes { get; set; }

        public bool Locked { get => _locked; set => Set(ref _locked, value); }

        public bool IsWorking => Status == VcItemStatus.Working;
        public bool CanRun => Status is VcItemStatus.Ready or VcItemStatus.Failed or VcItemStatus.Cancelled;
        public bool IsTrimmed => TrimStart is > 0.05 || (TrimEnd is double e && Duration is double d && e < d - 0.05);
        public string DurationText => Duration is double d ? VcText.FormatTime(d) : "";
        public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "";

        /// <summary>"Whole video", or "00:12 – 01:40".</summary>
        public string TrimText => !IsTrimmed || Duration is not double d
            ? "Whole video"
            : $"{VcText.FormatTime(TrimStart ?? 0)} – {VcText.FormatTime(TrimEnd ?? d)}";

        public string StatusText => Status switch
        {
            VcItemStatus.Reading => "Reading",
            VcItemStatus.Ready => "Ready",
            VcItemStatus.Unreadable => "Can't read",
            VcItemStatus.Working => "Working",
            VcItemStatus.Done => "Done",
            VcItemStatus.Failed => "Failed",
            _ => "Canceled",
        };

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>What a run does. A zero <see cref="MaxHeight"/> or <see cref="MaxFps"/> keeps the original.</summary>
    public sealed record VcOptions
    {
        public VcMode Mode { get; init; } = VcMode.Quality;
        public VcQuality Quality { get; init; } = VcQuality.Balanced;

        /// <summary>For <see cref="VcMode.TargetSize"/>: the size of each output, in megabytes.</summary>
        public int TargetMb { get; init; } = 25;

        public int MaxHeight { get; init; }
        public int MaxFps { get; init; }
        public VcAudio Audio { get; init; } = VcAudio.Keep;

        /// <summary>0.5 to 4. 1 = normal.</summary>
        public double Speed { get; init; } = 1;
    }

    public sealed record VcProgress(int Finished, int Failed, int Total, double Percent, string? CurrentName);

    public sealed record VcRunResult(int Done, int Failed, int Cancelled, IReadOnlyList<string> Files, long SourceBytes, long ResultBytes, TimeSpan Elapsed);

    /// <summary>Remembered between sessions (video_compressor.json). Settings only: never a path.</summary>
    public sealed class VcSettings
    {
        public VcMode Mode { get; set; } = VcMode.Quality;
        public VcQuality Quality { get; set; } = VcQuality.Balanced;
        public int TargetMb { get; set; } = 25;
        public int MaxHeight { get; set; }
        public int MaxFps { get; set; }
        public VcAudio Audio { get; set; } = VcAudio.Keep;
        public double Speed { get; set; } = 1;
        public bool OpenWhenDone { get; set; }

        /// <summary>The Trim card is open. Closed by default so the list gets the room; the Keep button opens it.</summary>
    }

    public static class VcText
    {
        /// <summary>"01:23" or "1:02:03".</summary>
        public static string FormatTime(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var span = TimeSpan.FromSeconds(Math.Round(seconds));
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
        }

        /// <summary>"01:23.4" or "1:02:03.4": the trim dialog needs tenths.</summary>
        public static string FormatPrecise(double seconds)
        {
            if (seconds < 0) seconds = 0;
            long tenths = (long)Math.Round(seconds * 10);
            long whole = tenths / 10;
            int t = (int)(tenths % 10);
            long h = whole / 3600, m = whole / 60 % 60, s = whole % 60;
            return h > 0 ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{t}") : string.Create(CultureInfo.InvariantCulture, $"{m:00}:{s:00}.{t}");
        }

        public static string FormatBytes(long bytes)
        {
            double b = Math.Max(0, bytes);
            if (b < 1024) return $"{b:0} B";
            if (b < 1024 * 1024) return $"{b / 1024:0.#} KB";
            if (b < 1024L * 1024 * 1024) return $"{b / (1024 * 1024):0.#} MB";
            return $"{b / (1024.0 * 1024 * 1024):0.##} GB";
        }

        public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";
    }
}
