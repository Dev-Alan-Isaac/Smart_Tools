using System.Globalization;
using Smart_Tools.WPF.Controllers;

namespace Smart_Tools.WPF.Models
{
    /// <summary>One line of the contents grid: a file or folder inside the archive, ready to show.</summary>
    public sealed class ArchiveRow
    {
        public ArchiveRow(ArchiveEntry entry)
        {
            Path = entry.Path;
            IsFolder = entry.IsFolder;
            Encrypted = entry.Encrypted;
            Size = entry.IsFolder ? 0 : entry.Size;
            Packed = entry.Packed ?? 0;
            Modified = entry.Modified ?? DateTime.MinValue;
            Name = entry.Name;
            string slashed = entry.Path.Replace('\\', '/').TrimEnd('/');
            int cut = slashed.LastIndexOf('/');
            Folder = cut < 0 ? "" : slashed[..cut];
            SizeText = IsFolder ? "" : OutputFolderManager.FormatBytes(Size);
            PackedText = entry.Packed is { } p && !IsFolder ? OutputFolderManager.FormatBytes(p) : "";
            ModifiedText = entry.Modified is { } m ? m.ToString("g", CultureInfo.CurrentCulture) : "";
        }

        /// <summary>The path exactly as 7-Zip lists it (used to pick files for a partial extract).</summary>
        public string Path { get; }

        public string Name { get; }

        /// <summary>The folder it sits in, inside the archive.</summary>
        public string Folder { get; }

        public bool IsFolder { get; }

        public bool Encrypted { get; }

        public long Size { get; }

        public long Packed { get; }

        public DateTime Modified { get; }

        public string SizeText { get; }

        public string PackedText { get; }

        public string ModifiedText { get; }
    }
}
