using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Voice Changer data. Plain data - no NAudio, no WPF. The effect
    //  processing itself is in Controllers/VoiceChangerDsp.cs.
    // =====================================================================

    public enum VoiceChangerMode
    {
        /// <summary>Change the voice in audio files, one new file each.</summary>
        Files,

        /// <summary>Change the microphone as you speak.</summary>
        LiveMic,
    }

    public enum VoiceChangerPreset
    {
        None,
        Chipmunk,
        DeepVoice,
        Robot,
        CaveEcho,
        Girl,
        Man,
        Grandma,
        Grandpa,
    }

    /// <summary>The effect settings from the sidebar.</summary>
    public sealed record VoiceChangerEffect(
        double PitchSemitones,
        bool Robot,
        bool Echo,
        double EchoDelayMs,
        double EchoFeedback,
        double EchoMix)
    {
        public static VoiceChangerEffect Neutral => new(0, false, false, 220, 0.35, 0.35);

        public bool IsNeutral => Math.Abs(PitchSemitones) < 0.05 && !Robot && !Echo;
    }

    /// <summary>A microphone Windows lists (the name is what's remembered).</summary>
    public sealed record VoiceChangerDevice(int Number, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>One audio file on the Files card.</summary>
    public sealed class VoiceChangerSource
    {
        public required string FullPath { get; init; }
        public string Name => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public VoiceChangerGroup? Group { get; set; }
    }

    /// <summary>
    /// One row on the Files card: a folder that was added (with or without
    /// its subfolders), or a set of loose files. Taking the row off removes
    /// its files; changing "Include subfolders" reads a folder row again.
    /// </summary>
    public sealed class VoiceChangerGroup : INotifyPropertyChanged
    {
        public string? FolderPath { get; init; }
        public bool IsFolder { get; init; }

        private bool _includeSubfolders;
        public bool IncludeSubfolders
        {
            get => _includeSubfolders;
            set { _includeSubfolders = value; Refresh(); }
        }

        public List<VoiceChangerSource> Items { get; } = new();

        public string Name => FolderPath == null
            ? "Several folders"
            : Path.GetFileName(FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FolderPath;

        public string Subtitle
        {
            get
            {
                string count = Items.Count == 1 ? "1 audio file" : $"{Items.Count:N0} audio files";
                if (!IsFolder) return count;
                return IncludeSubfolders ? $"Folder and subfolders · {count}" : $"Folder · {count}";
            }
        }

        public string ToolTip => FolderPath ?? "Audio files from more than one folder";

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>What a folder scan found.</summary>
    public sealed class VoiceChangerScan
    {
        public List<string> Files { get; } = new();
        public int InaccessibleFolders { get; set; }
        public bool Missing { get; set; }
    }

    /// <summary>One changed-voice file in the results list.</summary>
    public sealed class VoiceChangerTake : INotifyPropertyChanged
    {
        private string _filePath = "";
        private bool _isSaved;
        private bool _isPlaying;

        public required string SourceName { get; init; }
        public required string EffectSummary { get; init; }
        public TimeSpan Duration { get; init; }

        /// <summary>True when the result was turned down so it wouldn't clip (the effect pushed it past full scale).</summary>
        public bool WasLimited { get; init; }

        public string FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        public string DisplayName => Path.GetFileName(FilePath);

        public bool IsSaved
        {
            get => _isSaved;
            set { _isSaved = value; OnPropertyChanged(); OnPropertyChanged(nameof(Details)); }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set { if (_isPlaying != value) { _isPlaying = value; OnPropertyChanged(); } }
        }

        /// <summary>"from song.mp3 · +5 st · Robot · 0:42 · not saved yet"</summary>
        public string Details =>
            $"from {SourceName} · {EffectSummary} · {(int)Duration.TotalMinutes}:{Duration.Seconds:00}"
            + (WasLimited ? " · level lowered to avoid clipping" : "")
            + (IsSaved ? "" : " · not saved yet");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Remembered between sessions (voice_changer.json in %AppData%\Smart Tools). No paths; enums by name.</summary>
    public sealed class VoiceChangerSettings
    {
        public VoiceChangerMode Mode { get; set; } = VoiceChangerMode.Files;
        public double PitchSemitones { get; set; }
        public bool Robot { get; set; }
        public bool Echo { get; set; }
        public double EchoDelayMs { get; set; } = 220;
        public double EchoFeedback { get; set; } = 0.35;
        public double EchoMix { get; set; } = 0.35;
        public bool IncludeSubfolders { get; set; } = true;
        public string InputDevice { get; set; } = "";

        /// <summary>Where the live voice goes, by name: "" = Windows' default speakers/headphones, or e.g. "CABLE Input (VB-Audio Virtual Cable)" so other apps can use it as a microphone.</summary>
        public string OutputDevice { get; set; } = "";

        /// <summary>When the live voice goes to a virtual cable: also play it on the default speakers/headphones.</summary>
        public bool HearYourself { get; set; } = true;
    }
}
