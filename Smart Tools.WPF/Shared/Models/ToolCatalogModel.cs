namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Tool catalog data: the sections (categories) and tools the main
    //  window and the Home page are built from. Plain data - no WPF. Icons
    //  are MaterialDesign PackIconKind names and colours are hex strings;
    //  WPF bindings convert both (string -> PackIconKind / Brush).
    // =====================================================================

    /// <summary>
    /// Looks up the catalog's visible text in the active language. The app sets <see cref="Lookup"/> at
    /// startup (to a resource lookup); without it - or when a key is missing - the English text written
    /// in the catalog is used. Keys: Str_Tool_&lt;id&gt;_Name/_Title/_Description, Str_Cat_&lt;id&gt;_Title/_Blurb.
    /// </summary>
    public static class CatalogText
    {
        public static Func<string, string?>? Lookup { get; set; }

        public static string Get(string key, string fallback) => Lookup?.Invoke(key) ?? fallback;
    }

    /// <summary>A section of the sidebar and a card on the Home page.</summary>
    public sealed class ToolCategoryInfo
    {
        private readonly string _title = "";
        private readonly string _blurb = "";

        public required string Id { get; init; }
        public required string Title { get => CatalogText.Get($"Str_Cat_{Id}_Title", _title); init => _title = value; }

        /// <summary>PackIconKind name.</summary>
        public required string Icon { get; init; }

        /// <summary>Data color for the card badge and tool icons (not a theme token).</summary>
        public required string AccentHex { get; init; }

        /// <summary>One line under the card title.</summary>
        public string Blurb { get => CatalogText.Get($"Str_Cat_{Id}_Blurb", _blurb); init => _blurb = value; }

        /// <summary>Extra words search should match for every tool in the section (other languages too: "musica").</summary>
        public string Keywords { get; init; } = "";

        /// <summary>Filled in by the catalog: the tools in this section, in sidebar order.</summary>
        public IReadOnlyList<ToolInfo> Tools { get; internal set; } = Array.Empty<ToolInfo>();

        public string CountLabel => $"{Tools.Count} {CatalogText.Get(Tools.Count == 1 ? "Str_Catalog_Tool" : "Str_Catalog_Tools", Tools.Count == 1 ? "tool" : "tools")}";
        public string UpperTitle => Title.ToUpperInvariant();
    }

    /// <summary>One tool (one page of the app).</summary>
    public sealed class ToolInfo
    {
        private readonly string _name = "";
        private readonly string? _title;
        private readonly string _description = "";

        /// <summary>Stable id ("instrument-tuner") - used for Recent and the view registry; never shown.</summary>
        public required string Id { get; init; }

        /// <summary>Short name for the sidebar and tiles ("Spectrogram").</summary>
        public required string Name { get => CatalogText.Get($"Str_Tool_{Id}_Name", _name); init => _name = value; }

        /// <summary>Full name for the page header ("Spectrogram Generator"); defaults to <see cref="Name"/>.</summary>
        public string? Title
        {
            get => _title == null ? null : CatalogText.Get($"Str_Tool_{Id}_Title", _title);
            init => _title = value;
        }

        /// <summary>The names as written in the catalog (English), so search finds a tool by either language.</summary>
        public string EnglishNames => _name + " " + _title;

        /// <summary>PackIconKind name.</summary>
        public required string Icon { get; init; }

        public required string CategoryId { get; init; }

        /// <summary>One line on the Home tile.</summary>
        public string Description { get => CatalogText.Get($"Str_Tool_{Id}_Description", _description); init => _description = value; }

        /// <summary>Extra words search should match ("guitar", "mp3"...).</summary>
        public string Keywords { get; init; } = "";

        /// <summary>Filled in by the catalog.</summary>
        public ToolCategoryInfo Category { get; internal set; } = null!;

        public string HeaderTitle => Title ?? Name;
        public string AccentHex => Category?.AccentHex ?? "#64748B";
        public string CategoryTitle => Category?.Title ?? "";
    }

    /// <summary>
    /// The main window's own settings (shell.json in %AppData%\Smart Tools):
    /// recently opened tools and which sidebar sections are open. Tool ids
    /// only - never a file or folder path.
    /// </summary>
    public sealed class ShellSettings
    {
        public List<string> RecentToolIds { get; set; } = new();

        /// <summary>Tools the person pinned (star), in the order they were pinned.</summary>
        public List<string> FavoriteToolIds { get; set; } = new();
        public List<string> ExpandedCategories { get; set; } = new();
    }
}
