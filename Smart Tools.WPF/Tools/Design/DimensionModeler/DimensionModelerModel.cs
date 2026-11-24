using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>What the Dimension Modeler remembers between sessions (dimension_modeler.json). No paths.</summary>
    public sealed class ModelerSettings
    {
        public string Template { get; set; } = "table";

        /// <summary>The sliders per template id, so switching between a table and a chair keeps both.</summary>
        public Dictionary<string, Dictionary<string, double>> Values { get; set; } = new();

        public LengthUnit Unit { get; set; } = LengthUnit.Millimetres;

        public bool ShowDimensions { get; set; } = true;

        /// <summary>10 means 1:10; 1 is full size.</summary>
        public double ScaleRatio { get; set; } = 1;

        public double ClearanceMm { get; set; } = 0.2;

        public double BedX { get; set; } = 220;

        public double BedY { get; set; } = 220;

        public double BedHeight { get; set; } = 250;

        /// <summary>The custom piece (template id "custom"): its name and boards, kept like the sliders are.</summary>
        public string CustomName { get; set; } = "";

        public List<CustomPartDto> CustomParts { get; set; } = new();
    }

    /// <summary>The four ways to save a piece.</summary>
    public enum ModelerExportMode
    {
        /// <summary>The whole piece as one OBJ (scaled by the print ratio, no clearance).</summary>
        AssembledObj,

        /// <summary>The whole piece as one STL, Z up.</summary>
        AssembledStl,

        /// <summary>Every board laid flat on plates, one OBJ per plate.</summary>
        PrintObj,

        /// <summary>Every board laid flat on plates, one STL per plate.</summary>
        PrintStl,
    }

    /// <summary>What an export wrote, and the boards that were too big for the bed and so were left out.</summary>
    public sealed record ModelerExportResult(IReadOnlyList<string> Files, IReadOnlyList<string> TooBig);
}
