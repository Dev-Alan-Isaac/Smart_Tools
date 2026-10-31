using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Logic behind the Input card (Shared/Views/Extentions/InputFolderCard): is a
    /// source folder still there and readable, and which folder does a drop
    /// mean. Pure I/O, no WPF; never throws.
    /// </summary>
    public static class InputFolderManager
    {
        /// <summary>Checks a source folder. Call off the UI thread (a network share can take seconds).</summary>
        public static InputFolderStatus Describe(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return new InputFolderStatus(InputFolderState.None);
            try
            {
                if (!Directory.Exists(folder)) return new InputFolderStatus(InputFolderState.Missing);
                using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
                entries.MoveNext();   // proves the folder can be listed
                return new InputFolderStatus(InputFolderState.Ready);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
            {
                return new InputFolderStatus(InputFolderState.Unreadable);
            }
        }

        /// <summary>The first dropped folder, or the folder of a dropped file (same rule as the Output card).</summary>
        public static string? FolderFromDrop(IEnumerable<string> paths) => OutputFolderManager.FolderFromDrop(paths);

        /// <summary>A folder's own name for display ("Photos"), or the full path for a drive root.</summary>
        public static string DisplayName(string folder) => OutputFolderManager.DisplayName(folder);

        /// <summary>The paths of a drop or dialog that still exist (files and folders), without repeats.</summary>
        public static List<string> ExistingPaths(IEnumerable<string> paths) =>
            paths.Where(p => !string.IsNullOrWhiteSpace(p) && (Directory.Exists(p) || File.Exists(p)))
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList();
    }
}
