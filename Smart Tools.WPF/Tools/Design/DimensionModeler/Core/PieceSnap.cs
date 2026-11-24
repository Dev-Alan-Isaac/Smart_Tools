using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>Which side of the other board the moving board goes against.</summary>
    public enum SnapFace
    {
        Left,       // -X
        Right,      // +X
        Below,      // -Y
        Above,      // +Y
        Behind,     // -Z
        InFront,    // +Z (the front of a piece faces +Z)
    }

    /// <summary>How the moving board lines up with the other one along the two directions it is not touching in.</summary>
    public enum SnapAlign
    {
        Start,
        Center,
        End,
    }
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Joining boards like building blocks: put one against a face of another, lined up at its start, centre or end.</summary>
    public static class PieceSnap
    {
        private static double Along(double movingSize, double targetMin, double targetSize, SnapAlign align) => align switch
        {
            SnapAlign.Center => targetMin + (targetSize - movingSize) / 2,
            SnapAlign.End => targetMin + targetSize - movingSize,
            _ => targetMin,
        };

        /// <summary>The moving board moved so that it touches <paramref name="target"/> on <paramref name="face"/>. Its size never changes.</summary>
        public static Part Place(Part moving, Part target, SnapFace face, SnapAlign align)
        {
            var s = moving.Size;
            var t = target;
            double x = Along(s.X, t.Min.X, t.Size.X, align), y = Along(s.Y, t.Min.Y, t.Size.Y, align), z = Along(s.Z, t.Min.Z, t.Size.Z, align);
            switch (face)
            {
                case SnapFace.Left: x = t.Min.X - s.X; break;
                case SnapFace.Right: x = t.Max.X; break;
                case SnapFace.Below: y = t.Min.Y - s.Y; break;
                case SnapFace.Above: y = t.Max.Y; break;
                case SnapFace.Behind: z = t.Min.Z - s.Z; break;
                case SnapFace.InFront: z = t.Max.Z; break;
            }
            return moving with { Min = new Vec3(x, y, z) };
        }

        /// <summary>The list with board <paramref name="index"/> snapped to board <paramref name="targetIndex"/> (unchanged for a bad pair).</summary>
        public static IReadOnlyList<Part> Apply(IReadOnlyList<Part> parts, int index, int targetIndex, SnapFace face, SnapAlign align)
        {
            if (index < 0 || index >= parts.Count || targetIndex < 0 || targetIndex >= parts.Count || index == targetIndex) return parts;
            return PieceEditor.Replace(parts, index, Place(parts[index], parts[targetIndex], face, align));
        }
    }
}
