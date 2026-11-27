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

        /// <summary>The grid a dragged board lands on, in mm; 0 = no grid.</summary>
        public double SnapGridMm { get; set; } = 10;

        /// <summary>Dragged boards jump to the faces of the other boards and to the floor.</summary>
        public bool MagnetOn { get; set; } = true;

        /// <summary>10 means 1:10; 1 is full size.</summary>
        public double ScaleRatio { get; set; } = 1;

        public double ClearanceMm { get; set; } = 0.2;

        public double BedX { get; set; } = 220;

        public double BedY { get; set; } = 220;

        public double BedHeight { get; set; } = 250;

        /// <summary>The custom piece (template id "custom"): its name and boards, kept like the sliders are.</summary>
        public string CustomName { get; set; } = "";

        public List<CustomPartDto> CustomParts { get; set; } = new();

        /// <summary>Colours chosen in the 3D view: piece id (template id or "custom") -> board name -> colour.</summary>
        public Dictionary<string, Dictionary<string, BoardPaint>> Paint { get; set; } = new();

        /// <summary>Groups changed on a preset: template id -> board name -> group ("" = in no group). The custom piece keeps its groups in its boards.</summary>
        public Dictionary<string, Dictionary<string, string>> GroupOverrides { get; set; } = new();

        /// <summary>The cut list beside the 3D view is folded away (before the shape builder; read once as <see cref="PanelHidden"/>).</summary>
        public bool CutListHidden { get; set; }

        // ---- 3D Modeler (shape builder). The fields above Template..CutListHidden are only read to move old settings over. ----

        /// <summary>The design on screen. Null in settings from before the shape builder: then it is made from the old fields.</summary>
        public SceneFileDto? Scene { get; set; }

        /// <summary>The example picked in the Examples card (its sliders are kept in <see cref="Values"/>).</summary>
        public string ExampleId { get; set; } = "bracket";

        /// <summary>The Properties / Objects / Parts panel beside the 3D view is folded away. Null = not set yet (then the old cut-list choice).</summary>
        public bool? PanelHidden { get; set; }

        /// <summary>The tab open in that panel: "properties", "objects" or "parts".</summary>
        public string PanelTab { get; set; } = "properties";
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
