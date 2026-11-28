namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// What a surface is made of, apart from its colour: null = the standard (matte, solid) look, <see cref="Crystal"/> = see-through and
    /// shiny, tinted by the colour. Set on a shape or a group like a colour: a part's own material wins over its group's. View only:
    /// STL/OBJ exports and printing do not carry it. Pure: no WPF.
    /// </summary>
    public static class SceneMaterial
    {
        public const string Crystal = "crystal";

        /// <summary>A known material id, or null (standard) for anything else.</summary>
        public static string? Normalize(string? id) => id?.Trim().ToLowerInvariant() switch
        {
            Crystal => Crystal,
            _ => null,
        };

        /// <summary>The look carried on a CSG polygon while a scene is joined: "material|colour", or just the colour when standard.</summary>
        internal static string? Tag(string? colour, string? material) => material == null ? colour : material + "|" + colour;

        /// <summary>The colour and material back out of a polygon's tag.</summary>
        internal static (string? Colour, string? Material) Split(string? tag)
        {
            if (tag == null) return (null, null);
            int bar = tag.IndexOf('|');
            if (bar < 0) return (tag, null);
            string colour = tag[(bar + 1)..];
            return (colour.Length == 0 ? null : colour, tag[..bar]);
        }
    }
}
