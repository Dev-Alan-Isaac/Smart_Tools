using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>A line of sight from the camera through a point of the screen.</summary>
    public readonly record struct Ray3(Vec3 Origin, Vec3 Direction);

    /// <summary>
    /// Where a dragged board lands. <see cref="Target"/> says, per axis (X, Y, Z), what it stuck to: the index of another board,
    /// <see cref="Floor"/>, or <see cref="None"/> (the grid or nothing).
    /// </summary>
    public sealed record SnapResult(Vec3 Min, int[] Target)
    {
        public const int None = -1;
        public const int Floor = -2;
    }
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Moving and stretching boards with the mouse, as maths only: picking a board under the pointer, turning pointer movement into a
    /// position, and snapping it - to the faces of the other boards (so they fit together like blocks), to the floor, and to a grid.
    /// </summary>
    public static class PieceDrag
    {
        private static double Get(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        private static Vec3 With(Vec3 v, int axis, double value) =>
            axis == 0 ? v with { X = value } : axis == 1 ? v with { Y = value } : v with { Z = value };

        // =================================================================
        //  Picking
        // =================================================================

        /// <summary>The distance along the ray at which it enters the box, or null when it misses (a start inside the box counts as 0).</summary>
        public static double? RayBox(Ray3 ray, Vec3 min, Vec3 max)
        {
            double near = 0, far = double.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                double o = Get(ray.Origin, axis), d = Get(ray.Direction, axis), lo = Get(min, axis), hi = Get(max, axis);
                if (Math.Abs(d) < 1e-12)
                {
                    if (o < lo || o > hi) return null;
                    continue;
                }
                double t1 = (lo - o) / d, t2 = (hi - o) / d;
                if (t1 > t2) (t1, t2) = (t2, t1);
                near = Math.Max(near, t1);
                far = Math.Min(far, t2);
                if (near > far) return null;
            }
            return near;
        }

        /// <summary>The closest board the ray goes through (its index), or -1.</summary>
        public static int Pick(IReadOnlyList<Part> parts, Ray3 ray, out double distance, Func<Part, bool>? skip = null)
        {
            int best = -1;
            distance = double.PositiveInfinity;
            for (int i = 0; i < parts.Count; i++)
            {
                if (skip?.Invoke(parts[i]) ?? false) continue;   // a hidden board cannot be picked
                if (RayBox(ray, parts[i].Min, parts[i].Max) is { } t && t < distance)
                {
                    best = i;
                    distance = t;
                }
            }
            return best;
        }

        public static Vec3 PointAt(Ray3 ray, double distance) => ray.Origin + ray.Direction * distance;

        /// <summary>Where the ray meets the horizontal plane at height <paramref name="y"/>; null when it is parallel or points away.</summary>
        public static Vec3? RayHitsHeight(Ray3 ray, double y)
        {
            if (Math.Abs(ray.Direction.Y) < 1e-6) return null;
            double t = (y - ray.Origin.Y) / ray.Direction.Y;
            return t <= 0 ? null : PointAt(ray, t);
        }

        /// <summary>
        /// How far along the line through <paramref name="point"/> in direction <paramref name="axis"/> (a unit vector) the ray comes
        /// closest to it. Null when the ray runs along the line, where the answer is meaningless.
        /// </summary>
        public static double? ClosestOnAxis(Ray3 ray, Vec3 point, Vec3 axis)
        {
            var w = ray.Origin - point;
            double a = Vec3.Dot(ray.Direction, ray.Direction), b = Vec3.Dot(ray.Direction, axis), c = Vec3.Dot(axis, axis);
            double d = Vec3.Dot(ray.Direction, w), e = Vec3.Dot(axis, w);
            double denominator = a * c - b * b;
            if (Math.Abs(denominator) < 1e-9) return null;
            return (a * e - b * d) / denominator;
        }

        // =================================================================
        //  Snapping
        // =================================================================

        private static double Grid(double value, double grid) => grid > 0 ? Math.Round(value / grid, MidpointRounding.AwayFromZero) * grid : value;

        /// <summary>
        /// The board's new lowest corner for a proposed one. Per axis: if a face of the board is within <paramref name="threshold"/> mm of
        /// a face of another board (touching it, or flush with it) or of the floor, it jumps there; otherwise it goes to the grid.
        /// </summary>
        public static SnapResult Move(IReadOnlyList<Part> parts, int index, Vec3 proposed, double grid, double threshold, bool magnet)
        {
            var size = parts[index].Size;
            var result = proposed;
            var target = new[] { SnapResult.None, SnapResult.None, SnapResult.None };
            for (int axis = 0; axis < 3; axis++)
            {
                double want = Get(proposed, axis), len = Get(size, axis);
                double best = double.PositiveInfinity, snapped = want;
                int bestTarget = SnapResult.None;
                void Try(double candidate, int who)
                {
                    double gap = Math.Abs(candidate - want);
                    if (gap <= threshold && gap < best) { best = gap; snapped = candidate; bestTarget = who; }
                }
                if (magnet)
                {
                    if (axis == 1) Try(0, SnapResult.Floor);   // first, so the floor wins a tie
                    for (int j = 0; j < parts.Count; j++)
                    {
                        if (j == index) continue;
                        double lo = Get(parts[j].Min, axis), hi = Get(parts[j].Max, axis);
                        Try(hi, j);          // my low face on its high face
                        Try(lo - len, j);    // my high face on its low face
                        Try(lo, j);          // flush at the low side
                        Try(hi - len, j);    // flush at the high side
                    }
                }
                if (bestTarget == SnapResult.None) snapped = Grid(want, grid);
                result = With(result, axis, snapped);
                target[axis] = bestTarget;
            }
            return new SnapResult(result, target);
        }

        /// <summary>
        /// A board with one face moved to <paramref name="proposedFace"/> (the opposite face stays put). The face snaps like a moved
        /// board's does, and the board never gets thinner than <see cref="PieceEditor.MinSize"/>.
        /// </summary>
        public static Part Resize(IReadOnlyList<Part> parts, int index, int axis, bool highFace, double proposedFace, double grid, double threshold, bool magnet)
        {
            var part = parts[index];
            double face = proposedFace, best = double.PositiveInfinity;
            bool stuck = false;
            void Try(double candidate)
            {
                double gap = Math.Abs(candidate - proposedFace);
                if (gap <= threshold && gap < best) { best = gap; face = candidate; stuck = true; }
            }
            if (magnet)
            {
                for (int j = 0; j < parts.Count; j++)
                {
                    if (j == index) continue;
                    Try(Get(parts[j].Min, axis));
                    Try(Get(parts[j].Max, axis));
                }
                if (axis == 1) Try(0);
            }
            if (!stuck) face = Grid(proposedFace, grid);

            double lo = Get(part.Min, axis), hi = Get(part.Max, axis);
            if (highFace) hi = Math.Max(face, lo + PieceEditor.MinSize);
            else lo = Math.Min(face, hi - PieceEditor.MinSize);
            return part with { Min = With(part.Min, axis, lo), Size = With(part.Size, axis, hi - lo) };
        }
    }
}
