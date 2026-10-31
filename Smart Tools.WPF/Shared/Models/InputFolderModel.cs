namespace Smart_Tools.WPF.Models
{
    /// <summary>What a check of an input (source) folder found.</summary>
    public enum InputFolderState
    {
        /// <summary>No folder chosen.</summary>
        None,
        /// <summary>The folder exists and can be read.</summary>
        Ready,
        /// <summary>The folder isn't there (deleted, renamed, drive removed).</summary>
        Missing,
        /// <summary>The folder exists but Windows won't let it be read.</summary>
        Unreadable,
    }

    /// <summary>Result of <c>InputFolderManager.Describe</c>.</summary>
    public sealed record InputFolderStatus(InputFolderState State)
    {
        public bool IsError => State is InputFolderState.Missing or InputFolderState.Unreadable;
    }

    /// <summary>How an <c>InputFolderCard</c> collects its input.</summary>
    public enum InputFolderMode
    {
        /// <summary>A queue of files and folders (Add files / Add folder / Clear).</summary>
        Files,
        /// <summary>A list of folders only (Add folder / Clear).</summary>
        Folders,
        /// <summary>One source folder (Choose / Open / Reset).</summary>
        SingleFolder,
    }
}
