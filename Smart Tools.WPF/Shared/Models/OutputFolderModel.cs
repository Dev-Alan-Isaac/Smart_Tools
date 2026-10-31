namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  The shared Output card's data. Plain data - no WPF. Used by every
    //  tool that writes results to a folder (converters, editors,
    //  recorders...). Folders are kept for the session only: never saved.
    // =====================================================================

    public enum OutputFolderState
    {
        /// <summary>Nothing chosen yet.</summary>
        None,

        /// <summary>Results go next to each source file ("Same folder as each file").</summary>
        SameAsSource,

        /// <summary>Exists and can be written to, with room to spare.</summary>
        Ready,

        /// <summary>Doesn't exist yet but its parent does, so it's made on the first save.</summary>
        WillBeCreated,

        /// <summary>Usable, but under 1 GB free.</summary>
        LowSpace,

        /// <summary>The folder (or the drive it was on) isn't there.</summary>
        Missing,

        /// <summary>Windows won't let the app write there (a drive root, Program Files, a read-only share).</summary>
        NotWritable,
    }

    /// <summary>What the card shows under the folder name.</summary>
    public sealed record OutputFolderStatus(OutputFolderState State, string Text, long? FreeBytes)
    {
        /// <summary>Saving can go ahead (results either have a folder or go next to their sources).</summary>
        public bool CanSave => State is OutputFolderState.Ready or OutputFolderState.WillBeCreated
                                     or OutputFolderState.LowSpace or OutputFolderState.SameAsSource;

        /// <summary>Shown in amber (still usable) rather than red.</summary>
        public bool IsWarning => State is OutputFolderState.LowSpace or OutputFolderState.WillBeCreated;

        public bool IsError => State is OutputFolderState.Missing or OutputFolderState.NotWritable;
    }
}
