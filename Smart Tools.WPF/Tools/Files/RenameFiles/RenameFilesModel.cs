using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Rename Files data model. Plain data - no WPF, no disk access.
    //  Every type starts with "Rename" so nothing clashes with other tools
    //  in this namespace. Choices are saved by name (not combo position),
    //  so reordering the XAML can't change what runs.
    // =====================================================================

    public enum RenameMethod
    {
        FindReplace,
        PrefixSuffix,
        NumberSequence,
        ChangeCase,
        TrimStrip,
        RegexReplace,
        RemoveCharacters,

        /// <summary>Strips leftovers of copies from the end of the name: "_1", "-1", " - Copy", a second "(2)".</summary>
        CleanSuffixes,

        /// <summary>Throws the old names away: every file gets <see cref="RenameOptions.NewName"/> plus a number ("album_001.png").</summary>
        ReplaceName,
    }

    public enum RenameCaseStyle
    {
        Upper,
        Lower,
        Title,
        Sentence,
        Camel,
        Pascal,
    }

    public enum RenameNumberPosition
    {
        Suffix,
        Prefix,
        ReplaceEntireName,
    }

    /// <summary>Which order files are numbered in.</summary>
    public enum RenameNumberOrder
    {
        /// <summary>By name, "2" before "10".</summary>
        Name,

        /// <summary>Oldest first - photos and recordings come out in the order they were taken.</summary>
        DateModified,
    }

    /// <summary>
    /// What happens to the extension. Replaces the old list of four fixed
    /// extensions (.jpg, .png, .pdf, .txt), which renamed every file to
    /// that type whatever it was.
    /// </summary>
    public enum RenameExtensionMode
    {
        Keep,
        Lower,
        Upper,

        /// <summary>Change to <see cref="RenameOptions.NewExtension"/> (the name only - the file isn't converted).</summary>
        Change,
    }

    public enum RenameStatus
    {
        Ready,

        /// <summary>The rules don't change this name.</summary>
        NoChange,

        /// <summary>The new name can't be used (empty, reserved by Windows, too long).</summary>
        Invalid,

        Renamed,
        Failed,
        Cancelled,
    }

    /// <summary>Every setting the sidebar supplies. Saved between sessions (no folders in here).</summary>
    public sealed class RenameOptions
    {
        public RenameMethod Method { get; set; } = RenameMethod.FindReplace;

        // Scan
        public bool IncludeSubfolders { get; set; } = true;

        /// <summary>Leave desktop.ini, Thumbs.db and other hidden or system files alone (the old version renamed them too).</summary>
        public bool SkipHiddenAndSystem { get; set; } = true;

        // Extension
        public RenameExtensionMode ExtensionMode { get; set; } = RenameExtensionMode.Keep;
        public string NewExtension { get; set; } = "";

        // Find & Replace
        public string FindText { get; set; } = "";
        public string ReplaceText { get; set; } = "";
        public bool MatchCase { get; set; }
        public bool WholeWordOnly { get; set; }

        // Prefix / Suffix
        public string Prefix { get; set; } = "";
        public string Suffix { get; set; } = "";
        public bool SuffixBeforeExtension { get; set; } = true;

        // Replace whole name (numbering below is shared with Number sequence)
        public string NewName { get; set; } = "";

        // Number sequence
        public int StartAt { get; set; } = 1;
        public int Padding { get; set; } = 3;
        public int Increment { get; set; } = 1;
        public string Separator { get; set; } = "_";
        public RenameNumberPosition NumberPosition { get; set; } = RenameNumberPosition.Suffix;
        public RenameNumberOrder NumberOrder { get; set; } = RenameNumberOrder.Name;

        /// <summary>Start again at <see cref="StartAt"/> in every folder.</summary>
        public bool NumberPerFolder { get; set; }

        // Change case
        public RenameCaseStyle CaseStyle { get; set; } = RenameCaseStyle.Upper;
        public bool ApplyCaseToExtension { get; set; }

        // Trim / strip
        public int TrimStart { get; set; }
        public int TrimEnd { get; set; }
        public bool StripLeadingTrailingSpaces { get; set; } = true;
        public bool CollapseRepeatedSpaces { get; set; }

        // Regex replace
        public string Pattern { get; set; } = "^(.*)$";
        public string Replacement { get; set; } = "$1";
        public bool RegexCaseInsensitive { get; set; } = true;
        public bool ReplaceAllMatches { get; set; } = true;

        // Remove characters (all off by default - the old view had all four ticked)
        public string CharsToRemove { get; set; } = "";
        public bool RemoveNumbers { get; set; }
        public bool RemoveSymbols { get; set; }
        public bool RemoveSpaces { get; set; }
        public bool RemoveDiacritics { get; set; }

        // Clean copy suffixes
        public bool StripUnderscoreCounter { get; set; } = true;
        public bool StripDashCounter { get; set; } = true;
        public bool StripCopyWords { get; set; } = true;

        /// <summary>A "(2)" is removed only when another "(n)" comes before it, so a lone "(1)" stays.</summary>
        public bool StripWindowsCounter { get; set; } = true;

        public RenameOptions Clone() => (RenameOptions)MemberwiseClone();
    }

    /// <summary>A file found in the chosen folders.</summary>
    public readonly record struct RenameSourceFile(string FullPath, DateTime Modified)
    {
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public string FileName => Path.GetFileName(FullPath);
    }

    public sealed class RenameScanResult
    {
        public List<RenameSourceFile> Files { get; } = new();
        public int InaccessibleFolders { get; set; }
        public int HiddenSkipped { get; set; }
        public int LinksSkipped { get; set; }
        public int MissingFolders { get; set; }
    }

    /// <summary>One row in the preview.</summary>
    public sealed class RenamePreviewItem : INotifyPropertyChanged
    {
        private RenameStatus _status;
        private string _message = "";

        public required string FullPath { get; init; }

        /// <summary>Current file name, with extension.</summary>
        public required string OriginalName { get; init; }

        /// <summary>New file name, with extension.</summary>
        public required string NewName { get; init; }

        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public string FolderName => Path.GetFileName(Folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Folder;
        public string NewFullPath => Path.Combine(Folder, NewName);

        /// <summary>The new extension ("" when there's none) - the Type column.</summary>
        public string Extension => RenameText.SplitName(NewName).Extension;

        public DateTime Modified { get; init; }

        /// <summary>" (2)" was added because the name was taken.</summary>
        public bool IsAdjusted { get; init; }

        public bool IsChanged => !string.Equals(OriginalName, NewName, StringComparison.Ordinal);

        public RenameStatus Status
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

        /// <summary>Reason or detail (status tooltip).</summary>
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
            RenameStatus.Ready => "Ready",
            RenameStatus.NoChange => "No change",
            RenameStatus.Invalid => "Can't use",
            RenameStatus.Renamed => "Renamed",
            RenameStatus.Failed => "Failed",
            RenameStatus.Cancelled => "Cancelled",
            _ => "",
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>The preview: every row, plus a problem with the settings themselves (an invalid pattern) when there is one.</summary>
    public sealed class RenamePlan
    {
        public List<RenamePreviewItem> Items { get; init; } = new();

        /// <summary>Shown above the grid; null when the settings are fine.</summary>
        public string? Error { get; init; }

        /// <summary>"IMG_0001.jpg → Holiday_001.jpg" - the first file that changes, for the sidebar.</summary>
        public string? Example { get; init; }
    }

    public enum RenamePhase { Scanning, Renaming, Undoing }

    public readonly record struct RenameProgress(RenamePhase Phase, int Processed, int Total, string CurrentFile)
    {
        public double? Percent => Total > 0 ? Processed * 100.0 / Total : null;
    }

    public sealed class RenameRunResult
    {
        public int Renamed { get; set; }
        public int Failed { get; set; }
        public int Cancelled { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Undo record for the last run, saved to disk so Undo works after a
    /// restart. Holds every move in order - including the stop-overs
    /// through a temporary name that swaps (a↔b) need - and is deleted once
    /// undone.
    /// </summary>
    public sealed class RenameJournal
    {
        public DateTime When { get; set; }
        public int FileCount { get; set; }
        public List<RenameJournalStep> Steps { get; set; } = new();
    }

    public sealed class RenameJournalStep
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }

    public sealed class RenameUndoResult
    {
        public int Restored { get; set; }

        /// <summary>Put back under " (2)" because the old name had been taken since.</summary>
        public int RestoredWithNewName { get; set; }

        /// <summary>No longer where the run left it (moved or deleted since).</summary>
        public int Missing { get; set; }

        public int Failed { get; set; }
        public List<string> Errors { get; } = new();

        /// <summary>Steps not undone yet (the undo was cancelled) - saved so Undo can finish later.</summary>
        public List<RenameJournalStep> Remaining { get; } = new();
    }

    /// <summary>Remembered between sessions (rename_files.json in %AppData%\Smart Tools). Never any folder paths.</summary>
    public sealed class RenameSettings
    {
        public RenameOptions Options { get; set; } = new();
        public bool AutoPreview { get; set; } = true;
    }

    public static class RenameText
    {
        /// <summary>
        /// Name and extension. ".gitignore" is all name - the old code saw an
        /// empty name there, so every rule marked such files invalid.
        /// </summary>
        public static (string Name, string Extension) SplitName(string fileName)
        {
            int dot = fileName.LastIndexOf('.');
            if (dot <= 0) return (fileName, "");
            return (fileName[..dot], fileName[dot..]);
        }

        public static string Plural(int n, string one, string many) => n == 1 ? one : many;
    }
}
