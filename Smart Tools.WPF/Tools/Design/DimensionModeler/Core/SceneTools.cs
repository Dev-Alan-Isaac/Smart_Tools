using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>What Copy keeps: the nodes as they were, and the imported/drawn surfaces they use.</summary>
    public sealed record SceneClip(IReadOnlyList<SceneNode> Nodes, IReadOnlyDictionary<string, MeshData> Meshes);
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The everyday tools of a modeler that are not about one shape: copy and paste, repeat in a row, scaling evenly. Pure: no WPF.</summary>
    public static class SceneTools
    {
        public const int MaxRepeat = 50;

        /// <summary>The nodes to copy (a part of a group comes out on its own) with the surfaces they need; null when there is nothing.</summary>
        public static SceneClip? Copy(Scene scene, IEnumerable<string> ids)
        {
            var nodes = ids.Distinct().Select(id => SceneEdit.Find(scene, id)).OfType<SceneNode>().ToList();
            // a node whose group is also copied would come twice: keep the outer one
            var inside = nodes.SelectMany(n => SceneEdit.All(new[] { n }).Skip(1)).Select(n => n.Id).ToHashSet();
            nodes = nodes.Where(n => !inside.Contains(n.Id)).ToList();
            if (nodes.Count == 0) return null;
            var meshes = new Dictionary<string, MeshData>();
            foreach (var shape in nodes.SelectMany(SceneEdit.Shapes))
                if (shape.MeshId != null && scene.Meshes.TryGetValue(shape.MeshId, out var mesh)) meshes[shape.MeshId] = mesh;
            return new SceneClip(nodes, meshes);
        }

        /// <summary>The copied nodes added as new top-level objects, moved by <paramref name="offset"/>.</summary>
        public static Scene Paste(Scene scene, SceneClip clip, Vec3 offset, out List<string> ids)
        {
            var next = SceneEdit.AddNodes(scene, clip.Nodes, out ids, clip.Meshes);
            return ids.Count == 0 ? scene : SceneEdit.Translate(next, ids, offset);
        }

        /// <summary>
        /// <paramref name="count"/> copies of the nodes in a row along <paramref name="axis"/> (0 X, 1 Y, 2 Z), each one the size of
        /// the picked box plus <paramref name="gap"/> further on (a negative gap overlaps them). The copies stay where the originals
        /// are (inside the same group). <paramref name="ids"/> gets the ids of every copy.
        /// </summary>
        public static Scene Repeat(Scene scene, IReadOnlyList<string> nodeIds, int count, int axis, double gap, out List<string> ids)
        {
            ids = new List<string>();
            var box = SceneEdit.BoundsOf(scene, nodeIds);
            if (box.IsEmpty || count < 1) return scene;
            count = Math.Min(count, MaxRepeat);
            double size = axis == 0 ? box.Size.X : axis == 1 ? box.Size.Y : box.Size.Z;
            double step = size + gap;
            var dir = axis == 0 ? new Vec3(1, 0, 0) : axis == 1 ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);
            var result = scene;
            IReadOnlyList<string> last = nodeIds;
            for (int i = 0; i < count; i++)
            {
                var next = SceneEdit.Duplicate(result, last, dir * step, out var made);
                if (made.Count == 0) break;   // the shape limit
                result = next;
                ids.AddRange(made);
                last = made;
            }
            return result;
        }

        /// <summary>
        /// Resizing with the proportions kept: the dragged side's new length scales all three sides by the same amount. The pulled
        /// face's opposite face stays; across, the box keeps its middle, and a box standing on the floor keeps standing on it.
        /// </summary>
        public static Bounds3 ScaleEvenly(Bounds3 start, Bounds3 pulled, int axis, bool highFace)
        {
            double Get(Vec3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
            double from = Get(start.Size, axis), to = Get(pulled.Size, axis);
            if (from <= 1e-9) return pulled;
            double k = to / from;
            var min = new double[3];
            var max = new double[3];
            for (int a = 0; a < 3; a++)
            {
                double lo = Get(start.Min, a), hi = Get(start.Max, a), len = (hi - lo) * k;
                if (a == axis)
                {
                    lo = Get(pulled.Min, a);
                    hi = Get(pulled.Max, a);
                }
                else if (a == 1)
                {
                    hi = lo + len;   // grows upward from where it stands
                }
                else
                {
                    double mid = (lo + hi) / 2;
                    lo = mid - len / 2;
                    hi = mid + len / 2;
                }
                min[a] = lo;
                max[a] = hi;
            }
            return new Bounds3(new Vec3(min[0], min[1], min[2]), new Vec3(max[0], max[1], max[2]));
        }

        /// <summary>The fixed point an even resize grows from, per axis (for drawing it while dragging).</summary>
        public static Vec3 EvenScaleCentre(Bounds3 start, int axis, bool highFace)
        {
            double Pick(int a, double lo, double hi) => a == axis ? (highFace ? lo : hi) : a == 1 ? lo : (lo + hi) / 2;
            return new Vec3(Pick(0, start.Min.X, start.Max.X), Pick(1, start.Min.Y, start.Max.Y), Pick(2, start.Min.Z, start.Max.Z));
        }
    }
}
