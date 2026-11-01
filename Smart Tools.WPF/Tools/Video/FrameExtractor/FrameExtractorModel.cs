using Smart_Tools.WPF.Controllers;
using System.ComponentModel;
using System.IO;

namespace Smart_Tools.WPF.Models
{
    /// <summary>A file or folder added on the Input card.</summary>
    public sealed class VideoSource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
        public int Count { get; set; }

        public string Name => Path.GetFileName(FullPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FullPath;
        public string Subtitle => IsFolder ? $"Folder · {Count} video{(Count == 1 ? "" : "s")}" : "Video";
    }

    /// <summary>One video in the list. The length is read in the background; a file that can't be read says why.</summary>
    public sealed class VideoItem : INotifyPropertyChanged
    {
        private double? _duration;
        private string? _problem;
        private bool _reading = true;

        public required string FullPath { get; init; }
        public string Name => Path.GetFileName(FullPath);

        public double? Duration
        {
            get => _duration;
            set { _duration = value; _reading = false; OnChanged(); OnChanged(nameof(Detail)); }
        }

        /// <summary>Why the video can't be used; null when it is fine (or not read yet).</summary>
        public string? Problem
        {
            get => _problem;
            set { _problem = value; _reading = false; OnChanged(); OnChanged(nameof(Detail)); OnChanged(nameof(HasProblem)); }
        }

        public bool HasProblem => _problem != null;

        public string Detail => _problem != null ? "Can't be read" : _duration is { } d ? "Length " + FrameExtractorManager.FormatTime(d) : _reading ? "Reading..." : "";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A moment chosen for "Pick moments", shown as a chip that can be removed.</summary>
    public sealed record MomentChip(double Seconds)
    {
        public string Label => FrameExtractorManager.FormatTime(Seconds);
    }
}
