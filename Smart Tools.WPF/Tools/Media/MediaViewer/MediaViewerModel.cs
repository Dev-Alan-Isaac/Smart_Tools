namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Media Viewer data model. Plain data - no WPF, no disk access.
    // =====================================================================

    public enum ViewerMediaKind
    {
        Image,
        Video,
        Audio,
    }

    /// <summary>One entry in the Media Viewer list.</summary>
    public sealed class MediaItem
    {
        public MediaItem(string path, ViewerMediaKind kind)
        {
            Path = path;
            Kind = kind;
            Name = System.IO.Path.GetFileName(path);
            Icon = kind switch
            {
                ViewerMediaKind.Image => "ImageOutline",
                ViewerMediaKind.Video => "MovieOpenOutline",
                _ => "MusicNote",
            };
        }

        public string Path { get; }
        public string Name { get; }
        public ViewerMediaKind Kind { get; }

        /// <summary>Name of a Material Design icon (bound to the list's icon).</summary>
        public string Icon { get; }
    }
}
