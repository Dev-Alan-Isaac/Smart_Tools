using System.ComponentModel;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// One row of an input card that takes files and folders (a file, or a folder that stands for the files inside it).
    /// Used by the tools that work on one document at a time but can also be pointed at many files (the formatters).
    /// </summary>
    public sealed class BatchSource : INotifyPropertyChanged
    {
        private bool _isCurrent;
        private int _count;

        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }

        /// <summary>For a folder: how many matching files it holds (set after the list was made).</summary>
        public int Count
        {
            get => _count;
            set { _count = value; OnChanged(nameof(Count)); OnChanged(nameof(Subtitle)); }
        }

        /// <summary>The file that is open in the editor now (highlighted in the list).</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set { if (_isCurrent != value) { _isCurrent = value; OnChanged(nameof(IsCurrent)); } }
        }

        public string Name => Path.GetFileName(FullPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FullPath;

        public string Subtitle
        {
            get
            {
                if (IsFolder)
                    return Loc.N(_count, "Str_Batch_FolderOne", "Folder · {0} file", "Str_Batch_FolderMany", "Folder · {0} files", _count);
                try
                {
                    var info = new FileInfo(FullPath);
                    return info.Exists ? FormatSize(info.Length) : "";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
            }
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / 1048576.0:0.#} MB",
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Turns the files and folders a user picked into the list of files to work on.</summary>
    public static class BatchFiles
    {
        /// <summary>
        /// The files for <paramref name="paths"/>: a file is taken as it is (if <paramref name="accept"/> likes its extension);
        /// a folder gives the matching files inside it, and inside its subfolders when asked. Stops at <paramref name="max"/>.
        /// <c>Inaccessible</c> counts folders that could not be read.
        /// </summary>
        public static (List<string> Files, int Inaccessible) Find(IEnumerable<string> paths, bool includeSubfolders, Func<string, bool> accept, int max)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int inaccessible = 0;

            void Add(string file)
            {
                if (found.Count < max && accept(file) && seen.Add(file)) found.Add(file);
            }

            foreach (string path in paths)
            {
                if (found.Count >= max) break;
                if (File.Exists(path)) { Add(path); continue; }
                if (!Directory.Exists(path)) continue;

                foreach (string file in SafeIo.EnumerateFiles(path, includeSubfolders, () => inaccessible++,
                             attributesToSkip: FileAttributes.System | FileAttributes.ReparsePoint))
                {
                    if (found.Count >= max) break;
                    Add(file);
                }
            }
            return (found, inaccessible);
        }
    }
}
