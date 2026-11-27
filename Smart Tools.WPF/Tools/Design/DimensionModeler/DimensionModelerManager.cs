using System.IO;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Settings and saving for the Dimension Modeler. No WPF types; the maths is in Core\.</summary>
    public static class DimensionModelerManager
    {
        private const string SettingsName = "dimension_modeler";

        public static ModelerSettings LoadSettings(string? path = null) => JsonSettings.Load<ModelerSettings>(SettingsName, path);

        public static void SaveSettings(ModelerSettings settings, string? path = null) => JsonSettings.Save(SettingsName, settings, path);

        public static string DefaultFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Smart Tools", "Models");

        public static string Extension(ModelerExportMode mode) =>
            mode is ModelerExportMode.AssembledObj or ModelerExportMode.PrintObj ? ".obj" : ".stl";

        /// <summary>"table.stl" for plate 2 of 3 becomes "table-plate2.stl"; a single plate keeps the name asked for.</summary>
        public static string PlatePath(string path, int number, int count) =>
            count <= 1 ? path : Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}-plate{number}{Path.GetExtension(path)}");

        /// <summary>
        /// Writes the piece. The whole-piece modes make one file at the print scale; the print modes plan the parts onto plates
        /// (scale, clearance, flat) and make one file per plate. A file that is already there is never overwritten: the new one gets " (2)".
        /// </summary>
        public static ModelerExportResult Export(ModelerExportMode mode, string path, PieceModel model, PrintSettings print)
        {
            path = Path.ChangeExtension(path, Extension(mode));
            bool obj = Extension(mode) == ".obj";
            var files = new List<string>();

            if (mode is ModelerExportMode.AssembledObj or ModelerExportMode.AssembledStl)
            {
                files.Add(Write(PieceTransform.Scale(model, print.Scale), path, obj));
                return new ModelerExportResult(files, Array.Empty<string>());
            }

            var plan = PrintPlanner.Plan(model, print);
            foreach (var plate in plan.Plates)
            {
                var plateModel = PrintPlanner.PlateModel(plate, $"{model.Name} plate {plate.Number}");
                files.Add(Write(plateModel, PlatePath(path, plate.Number, plan.Plates.Count), obj));
            }
            return new ModelerExportResult(files, plan.TooBig.Select(p => p.Name).ToList());
        }

        /// <summary>
        /// Writes the finished design (holes cut out, hidden shapes included, lone holes left out) as one OBJ or STL file, scaled down by
        /// the print ratio (10 = 1:10). A file that is already there is never overwritten: the new one gets " (2)". Returns the file
        /// written, or null when there is nothing solid to write.
        /// </summary>
        public static string? ExportScene(Scene scene, bool obj, string path, double ratio)
        {
            var objects = new SceneMeshBuilder().Build(scene, includeHidden: true).Where(o => !o.IsHole && o.Triangles.Count > 0).ToList();
            if (objects.Count == 0) return null;
            double scale = 1 / Math.Max(1, ratio);
            string target = SafeFileOps.UniquePath(Path.ChangeExtension(path, obj ? ".obj" : ".stl"));
            if (obj)
            {
                var named = objects.Select(o => (SceneEdit.Find(scene, o.NodeId)?.Name ?? "object", o.Triangles));
                AtomicFile.WriteAllText(target, SceneFiles.ToObj(scene.Name, named, scale));
            }
            else AtomicFile.WriteAllBytes(target, SceneFiles.ToStl(objects.SelectMany(o => o.Triangles.Select(t => t.Triangle)), scale));
            return target;
        }

        private static string Write(PieceModel model, string path, bool obj)
        {
            string target = SafeFileOps.UniquePath(path);
            if (obj) AtomicFile.WriteAllText(target, PieceExport.ToObj(model));
            else AtomicFile.WriteAllBytes(target, PieceExport.ToStlBinary(model));
            return target;
        }
    }
}
