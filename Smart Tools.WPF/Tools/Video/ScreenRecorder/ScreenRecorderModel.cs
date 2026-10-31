namespace Smart_Tools.WPF.Models
{
    /// <summary>What to record: a whole monitor (or all of them), one window, or a rectangle you drag out.</summary>
    public enum RecordTarget { Screen, Window, Area }

    /// <summary>Picture quality against file size. Maps to the encoder's CRF (lower = better and bigger).</summary>
    public enum RecordQuality { Small, Balanced, High }

    /// <summary>A rectangle on the virtual desktop, in pixels. X and Y can be negative when a monitor sits left of or above the main one.</summary>
    public sealed record RecordRegion(int X, int Y, int Width, int Height);

    /// <summary>A monitor to record. <see cref="Region"/> is null for "all screens".</summary>
    public sealed class RecordScreenInfo
    {
        public string Name { get; init; } = "";
        public RecordRegion? Region { get; init; }
        public bool IsPrimary { get; init; }
        public override string ToString() => Name;
    }

    /// <summary>A top-level window that can be recorded.</summary>
    public sealed class RecordWindowInfo
    {
        public long Handle { get; init; }
        public string Title { get; init; } = "";
        public override string ToString() => Title;
    }

    /// <summary>Everything a recording needs. <see cref="OutputPath"/> must be a free file name (the tool never overwrites).</summary>
    public sealed record RecordOptions
    {
        public RecordTarget Target { get; init; } = RecordTarget.Screen;

        /// <summary>For <see cref="RecordTarget.Screen"/> (one monitor) and <see cref="RecordTarget.Area"/>. Null with Screen = all monitors.</summary>
        public RecordRegion? Region { get; init; }

        public long WindowHandle { get; init; }
        public string WindowTitle { get; init; } = "";

        /// <summary>15, 30, or 60.</summary>
        public int Fps { get; init; } = 30;

        public RecordQuality Quality { get; init; } = RecordQuality.Balanced;
        public bool ShowCursor { get; init; } = true;

        /// <summary>The DirectShow name of a microphone to record with the picture, or null for no sound.</summary>
        public string? Microphone { get; init; }

        /// <summary>Leave out the time a Windows security prompt (UAC) or the lock screen is up: it can't be recorded anyway.</summary>
        public bool SkipSecurityPrompts { get; init; } = true;

        public string OutputPath { get; init; } = "";
    }

    /// <summary>How a recording ended. <see cref="Message"/> is plain words for a failure, or a note for a success.</summary>
    public sealed record RecordingResult(bool Ok, string Path, long Bytes, TimeSpan Duration, string Message);

    /// <summary>A finished recording in this session's list.</summary>
    public sealed class RecordingEntry
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public long Bytes { get; init; }
        public TimeSpan Duration { get; init; }
        public DateTime When { get; init; }

        /// <summary>"01:23" and "14.2 MB", worked out by the view when the entry is made.</summary>
        public string DurationText { get; init; } = "";
        public string SizeText { get; init; } = "";
    }

    /// <summary>What the tool remembers (%AppData%\Smart Tools\screen-recorder.json). Settings only: never a path or a window title.</summary>
    public sealed class ScreenRecorderSettings
    {
        public RecordTarget Target { get; set; } = RecordTarget.Screen;
        public int Fps { get; set; } = 30;
        public RecordQuality Quality { get; set; } = RecordQuality.Balanced;
        public bool ShowCursor { get; set; } = true;
        public bool MinimizeWhileRecording { get; set; } = true;
        public bool SkipSecurityPrompts { get; set; } = true;
    }
}
