namespace Smart_Tools.WPF.Models
{
    public enum FolderTreeFormat { Text, Markdown, Html, Json, Xml, Yaml, Csv, Paths, Pdf, Image }

    public sealed class FolderTreeOptions
    {
        public string Folder { get; set; } = "";
        public FolderTreeFormat Format { get; set; } = FolderTreeFormat.Text;
        public bool IncludeFiles { get; set; } = true;
        /// <summary>Levels below the chosen folder to list; 0 means all of them.</summary>
        public int MaxDepth { get; set; }
        public bool ShowSize { get; set; } = true;
        /// <summary>Show how many files each folder holds (everything below it, like its size).</summary>
        public bool ShowCount { get; set; } = true;
        public bool ShowDate { get; set; }
        public bool IncludeHidden { get; set; }
        /// <summary>Leave out .git, node_modules, bin, obj and the like.</summary>
        public bool SkipNoise { get; set; } = true;
    }

    public sealed class FolderTreeNode
    {
        public required string Name { get; init; }
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
        /// <summary>A file's length, or the total of everything inside a folder.</summary>
        public long Size { get; set; }
        /// <summary>For a folder: how many files are inside it, in every level below (counted even past the depth limit, like <see cref="Size"/>). 0 for a file.</summary>
        public long FileCount { get; set; }
        public DateTime Modified { get; init; }
        /// <summary>Why a folder's content isn't listed (no access, a link that isn't followed).</summary>
        public string? Note { get; set; }
        public List<FolderTreeNode> Children { get; } = new();
    }

    public sealed class FolderTreeResult
    {
        public required FolderTreeNode Root { get; init; }
        public int Folders { get; set; }
        public int Files { get; set; }
        public long Bytes { get; set; }
        /// <summary>Folders that couldn't be read.</summary>
        public int Unreadable { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    public sealed record FolderTreeProgress(int Folders, int Files, string Current);
}
