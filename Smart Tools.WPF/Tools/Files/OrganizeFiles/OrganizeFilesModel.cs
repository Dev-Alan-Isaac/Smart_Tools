using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Organize Files data model. Plain data - no WPF brushes, no ffprobe,
    //  no Magick. Status colours come from DataTriggers in the view (the
    //  old hard-coded light brushes were unreadable in the dark theme).
    // =====================================================================

    /// <summary>What a step sorts by. Document* stays for saved settings but isn't offered (no document library).</summary>
    public enum OrganizeCriterion
    {
        // General
        Category,
        Extension,
        FileType,
        Size,
        DateCreated,
        DateModified,
        Year,
        Month,
        FirstLetter,

        // Video
        VideoResolution,
        VideoDuration,
        VideoCodec,
        VideoFrameRate,

        // Audio
        AudioBitrate,
        AudioDuration,
        AudioCodec,
        AudioChannels,

        // Document (not offered)
        DocumentPageCount,
        DocumentAuthor,
        DocumentTitle,
        DocumentWordCount,

        // Image
        ImageResolution,
        ImageDimensions,
        ImageFormat,
        ImageColorDepth,
    }

    /// <summary>
    /// How files are laid out in the destination. Replaces the two
    /// "Flatten" / "Preserve structure" checkboxes that had to switch each
    /// other off.
    /// </summary>
    public enum OrganizeLayout
    {
        /// <summary>One folder level per step (e.g. Images\2024).</summary>
        Rules,

        /// <summary>Everything straight into the destination folder.</summary>
        Flatten,

        /// <summary>Recreate each file's subfolder under the destination.</summary>
        KeepStructure,
    }

    public enum OrganizeStatus
    {
        Ready,

        /// <summary>Already where it would go.</summary>
        InPlace,

        Moved,
        Copied,

        /// <summary>An identical file was already at the destination.</summary>
        Duplicate,

        Failed,
        Cancelled,
    }

    public sealed class OrganizeStep
    {
        public OrganizeCriterion Criterion { get; set; }

        /// <summary>
        /// Files the step can't sort (unknown extension, a photo under a
        /// "Video - Codec" step...) go into "Other". Off: unknown extensions
        /// get their own folder, and files a media step doesn't apply to
        /// skip that level.
        /// </summary>
        public bool GroupUnknownAsOther { get; set; } = true;
    }

    /// <summary>A category from Settings (name + extensions), passed in so the planner doesn't depend on the settings store.</summary>
    public sealed record OrganizeCategory(string Name, IReadOnlyList<string> Extensions)
    {
        public static OrganizeCategory Parse(string name, string? extensions) =>
            new(name, (extensions ?? "")
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .ToList());
    }

    public sealed class OrganizeOptions
    {
        public string SourceFolder { get; init; } = "";

        /// <summary>Where organized files go (the source folder itself when "same folder" is chosen).</summary>
        public string DestinationFolder { get; init; } = "";

        public OrganizeLayout Layout { get; init; } = OrganizeLayout.Rules;
        public List<OrganizeStep> Steps { get; init; } = new();

        public bool IncludeSubfolders { get; init; }

        /// <summary>Leave hidden and system files alone (desktop.ini, Thumbs.db, .git...). The old version moved them too.</summary>
        public bool SkipHiddenAndSystem { get; init; } = true;

        public bool MoveFiles { get; init; } = true;

        /// <summary>After moving, delete source folders the run emptied (never the source folder itself).</summary>
        public bool RemoveEmptyFolders { get; init; } = true;

        /// <summary>"🖼️ Images" instead of "Images" for category folders.</summary>
        public bool EmojiFolders { get; init; } = true;

        public IReadOnlyList<OrganizeCategory> Categories { get; init; } = Array.Empty<OrganizeCategory>();

        /// <summary>Files moved or copied at once. 0 = automatic (4 for same-drive moves, 2 otherwise).</summary>
        public int MaxDegreeOfParallelism { get; init; }
    }

    public enum OrganizePhase { Scanning, Planning, Organizing, Undoing }

    public readonly record struct OrganizeProgress(OrganizePhase Phase, int Processed, int Total, string CurrentFile)
    {
        public double? Percent => Total > 0 ? Processed * 100.0 / Total : null;
    }

    /// <summary>A file found in the source folder.</summary>
    public readonly record struct OrganizeScannedFile(string FullPath, long SizeBytes, DateTime Created, DateTime Modified);

    /// <summary>What ffprobe says about a video or audio file.</summary>
    public sealed record OrganizeMediaFacts(
        double DurationSeconds,
        string VideoCodec,
        int Width,
        int Height,
        double FrameRate,
        string AudioCodec,
        int AudioChannels,
        long AudioBitrateBps);

    /// <summary>What an image header says (Magick.NET ping - no pixels decoded).</summary>
    public sealed record OrganizeImageFacts(int Width, int Height, string Format, int Depth);

    /// <summary>One row in the preview grid.</summary>
    public sealed class OrganizeFileItem : INotifyPropertyChanged
    {
        private OrganizeStatus _status = OrganizeStatus.Ready;
        private string _message = "";
        private string _destination = "";

        public required string FullPath { get; init; }
        public required string Name { get; init; }
        public required long SizeBytes { get; init; }
        public DateTime Modified { get; init; }
        public MediaKind Kind { get; init; }

        public string Extension => Path.GetExtension(Name);
        public string Size => OrganizeText.FormatBytes(SizeBytes);
        public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm");

        /// <summary>Where the file is now, relative to the source folder ("" = top level).</summary>
        public string SourceFolderDisplay { get; init; } = "";

        /// <summary>Full destination path. Changes if a name clash is found while running.</summary>
        public string Destination
        {
            get => _destination;
            set
            {
                _destination = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DestinationName));
                OnPropertyChanged(nameof(IsRenamed));
            }
        }

        /// <summary>Destination folder relative to the destination root - the grid groups by this.</summary>
        public string DestinationFolderDisplay { get; set; } = "";

        public string DestinationName => Path.GetFileName(_destination);
        public bool IsRenamed => _destination.Length > 0 && !string.Equals(DestinationName, Name, StringComparison.OrdinalIgnoreCase);

        public OrganizeStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        /// <summary>Reason / detail (tooltip).</summary>
        public string Message
        {
            get => _message;
            set
            {
                _message = value;
                OnPropertyChanged();
            }
        }

        public string StatusText => _status switch
        {
            OrganizeStatus.Ready => "Ready",
            OrganizeStatus.InPlace => "In place",
            OrganizeStatus.Moved => "Moved",
            OrganizeStatus.Copied => "Copied",
            OrganizeStatus.Duplicate => "Already there",
            OrganizeStatus.Failed => "Failed",
            OrganizeStatus.Cancelled => "Cancelled",
            _ => "",
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One step card in the sidebar (bound by the view).</summary>
    public sealed class OrganizeStepRow : INotifyPropertyChanged
    {
        private OrganizeCriterion _criterion;
        private bool _groupUnknownAsOther = true;
        private int _number = 1;
        private bool _canMoveUp;
        private bool _canMoveDown;

        public OrganizeCriterion Criterion
        {
            get => _criterion;
            set
            {
                if (_criterion == value) return;
                _criterion = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Hint));
                OnPropertyChanged(nameof(HasHint));
            }
        }

        /// <summary>What a video/audio/image rule does with other files - shown under the card.</summary>
        public string Hint => _criterion switch
        {
            >= OrganizeCriterion.VideoResolution and <= OrganizeCriterion.VideoFrameRate => "Sorts videos only (read with ffprobe).",
            >= OrganizeCriterion.AudioBitrate and <= OrganizeCriterion.AudioChannels => "Sorts audio files only (read with ffprobe).",
            >= OrganizeCriterion.ImageResolution and <= OrganizeCriterion.ImageColorDepth => "Sorts images only.",
            _ => "",
        };

        public bool HasHint => Hint.Length > 0;

        public bool GroupUnknownAsOther
        {
            get => _groupUnknownAsOther;
            set
            {
                if (_groupUnknownAsOther == value) return;
                _groupUnknownAsOther = value;
                OnPropertyChanged();
            }
        }

        public int Number
        {
            get => _number;
            set
            {
                _number = value;
                OnPropertyChanged();
            }
        }

        public bool CanMoveUp
        {
            get => _canMoveUp;
            set
            {
                _canMoveUp = value;
                OnPropertyChanged();
            }
        }

        public bool CanMoveDown
        {
            get => _canMoveDown;
            set
            {
                _canMoveDown = value;
                OnPropertyChanged();
            }
        }

        public OrganizeStep ToStep() => new() { Criterion = _criterion, GroupUnknownAsOther = _groupUnknownAsOther };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>An entry in the step's criterion list.</summary>
    public sealed record OrganizeCriterionChoice(OrganizeCriterion Criterion, string Label, string Group, string Example);

    public sealed class OrganizeScanResult
    {
        public List<OrganizeScannedFile> Files { get; } = new();
        public int InaccessibleFolders { get; set; }
        public int LinksSkipped { get; set; }
        public int HiddenSkipped { get; set; }

        /// <summary>Files under the destination folder, left out so a second run doesn't re-sort its own output.</summary>
        public int InDestinationSkipped { get; set; }
    }

    public sealed class OrganizeRunResult
    {
        public int Moved { get; set; }
        public int Copied { get; set; }
        public int Duplicates { get; set; }
        public int Failed { get; set; }
        public int Cancelled { get; set; }
        public long Bytes { get; set; }
        public int FoldersRemoved { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>Undo record for the last run, saved to disk so Undo still works after a restart.</summary>
    public sealed class OrganizeJournal
    {
        public DateTime When { get; set; }
        public bool Moved { get; set; }
        public string SourceFolder { get; set; } = "";
        public List<OrganizeJournalEntry> Entries { get; set; } = new();

        /// <summary>Destination folders the run created (removed again on undo when empty).</summary>
        public List<string> CreatedFolders { get; set; } = new();
    }

    public sealed class OrganizeJournalEntry
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }

    public sealed class OrganizeUndoResult
    {
        public int Restored { get; set; }
        public int Missing { get; set; }
        public int Renamed { get; set; }
        public int Failed { get; set; }
        public List<string> Errors { get; } = new();
    }

    /// <summary>
    /// Remembered between sessions (organize_files.json in %AppData%\Smart Tools).
    /// Only how to organize - never which folders: the source and destination
    /// you pick are kept for this session only and aren't written to disk.
    /// </summary>
    public sealed class OrganizeSettings
    {
        public bool UseSourceAsDestination { get; set; } = true;
        public OrganizeLayout Layout { get; set; } = OrganizeLayout.Rules;
        public List<OrganizeStep> Steps { get; set; } = new() { new OrganizeStep { Criterion = OrganizeCriterion.Category } };
        public bool IncludeSubfolders { get; set; }
        public bool SkipHiddenAndSystem { get; set; } = true;
        public bool MoveFiles { get; set; } = true;
        public bool RemoveEmptyFolders { get; set; } = true;
        public bool EmojiFolders { get; set; } = true;
    }

    public static class OrganizeText
    {
        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
        }
    }
}