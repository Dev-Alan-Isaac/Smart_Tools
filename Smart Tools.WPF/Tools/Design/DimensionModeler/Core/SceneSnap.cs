using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>What a dragged object lined up with on one axis.</summary>
    public enum SnapKind
    {
        /// <summary>Stands on the floor (Y = 0).</summary>
        Floor,
        /// <summary>A face against a face of the other object (side by side, or on top of it).</summary>
        Touch,
        /// <summary>A side flush with the other's side (same left, same right...), so corners line up.</summary>
        Edge,
        /// <summary>Centre on the other's centre, a side on its centre, or its centre on one of my sides.</summary>
        Centre,
    }

    /// <summary>One snap: on <see cref="Axis"/> (0 X, 1 Y, 2 Z) the dragged box's point and the other's point meet at <see cref="At"/>.</summary>
    public sealed record SnapGuide(int Axis, SnapKind Kind, int Other, double At);

    /// <summary>The move to apply (already snapped) and what it snapped to, per axis.</summary>
    public sealed record SnapMove(Vec3 Delta, IReadOnlyList<SnapGuide> Guides);
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Snapping a dragged object (its box) to the others, as maths only. Per axis that is being dragged, the object's low side, centre
    /// and high side are tried against each other box's low side, centre and high side, plus touching (face on face) and, for height,
    /// the floor. The nearest one within the threshold wins; with none in reach the object's low corner goes to the grid.
    /// </summary>
    public static class SceneSnap
    {
        private static double Get(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        private static Vec3 With(Vec3 v, int axis, double value) =>
            axis == 0 ? v with { X = value } : axis == 1 ? v with { Y = value } : v with { Z = value };

        private static double Grid(double value, double grid) => grid > 0 ? Math.Round(value / grid, MidpointRounding.AwayFromZero) * grid : value;

        /// <param name="moving">The dragged object's box where the drag started.</param>
        /// <param name="proposed">How far the mouse moved it.</param>
        /// <param name="others">The boxes it can snap to (index = <see cref="SnapGuide.Other"/>).</param>
        /// <param name="axes">Which axes the drag moves (X and Z over the floor, Y when lifting); the others stay as they are.</param>
        /// <param name="threshold">How close (mm) a match must be to snap.</param>
        /// <param name="magnet">False: only the grid.</param>
        public static SnapMove Move(Bounds3 moving, Vec3 proposed, IReadOnlyList<Bounds3> others, bool[] axes, double grid, double threshold, bool magnet)
        {
            var delta = proposed;
            var guides = new List<SnapGuide>();
            for (int axis = 0; axis < 3; axis++)
            {
                if (!axes[axis])
                {
                    delta = With(delta, axis, 0);
                    continue;
                }
                double len = Get(moving.Size, axis);
                double want = Get(moving.Min, axis) + Get(proposed, axis);   // where the low side would be
                double bestGap = double.PositiveInfinity, bestMin = want;
                SnapGuide? best = null;

                void Try(double newMin, SnapKind kind, int other, double at)
                {
                    double gap = Math.Abs(newMin - want);
                    // ties: the earlier (stronger) kind wins, so floor beats touch beats edge beats centre
                    if (gap > threshold || gap >= bestGap - 1e-9) return;
                    bestGap = gap;
                    bestMin = newMin;
                    best = new SnapGuide(axis, kind, other, at);
                }

                if (magnet)
                {
                    if (axis == 1) Try(0, SnapKind.Floor, -1, 0);
                    for (int j = 0; j < others.Count; j++)
                    {
                        var o = others[j];
                        if (o.IsEmpty) continue;
                        double lo = Get(o.Min, axis), hi = Get(o.Max, axis), mid = (lo + hi) / 2;
                        Try(hi, SnapKind.Touch, j, hi);                  // my low side on its high side
                        Try(lo - len, SnapKind.Touch, j, lo);            // my high side on its low side
                        Try(lo, SnapKind.Edge, j, lo);                   // flush low sides
                        Try(hi - len, SnapKind.Edge, j, hi);             // flush high sides
                        Try(mid - len / 2, SnapKind.Centre, j, mid);     // centre on centre
                        Try(mid, SnapKind.Centre, j, mid);               // my low side on its centre
                        Try(mid - len, SnapKind.Centre, j, mid);         // my high side on its centre
                        Try(lo - len / 2, SnapKind.Centre, j, lo);       // my centre on its low side
                        Try(hi - len / 2, SnapKind.Centre, j, hi);       // my centre on its high side
                    }
                }
                if (best == null) bestMin = Grid(want, grid);
                else guides.Add(best);
                delta = With(delta, axis, bestMin - Get(moving.Min, axis));
            }
            return new SnapMove(delta, guides);
        }

        /// <summary>The smallest a side can be pulled in to (mm).</summary>
        public const double MinSide = 1;

        /// <summary>
        /// Pulling one face of a box (Alt+drag): the face goes to <paramref name="proposedFace"/> on <paramref name="axis"/> while the
        /// opposite face stays. It snaps to the other boxes' sides and centres and to the floor, else to the grid, and the box never gets
        /// thinner than <see cref="MinSide"/>. Returns the new box and what the face snapped to (null: the grid or nothing).
        /// </summary>
        public static (Bounds3 Box, SnapGuide? Guide) Face(Bounds3 box, int axis, bool highFace, double proposedFace,
            IReadOnlyList<Bounds3> others, double grid, double threshold, bool magnet)
        {
            double face = proposedFace, bestGap = double.PositiveInfinity;
            SnapGuide? guide = null;
            void Try(double candidate, SnapKind kind, int other)
            {
                double gap = Math.Abs(candidate - proposedFace);
                if (gap > threshold || gap >= bestGap - 1e-9) return;
                bestGap = gap;
                face = candidate;
                guide = new SnapGuide(axis, kind, other, candidate);
            }
            if (magnet)
            {
                if (axis == 1) Try(0, SnapKind.Floor, -1);
                for (int j = 0; j < others.Count; j++)
                {
                    var o = others[j];
                    if (o.IsEmpty) continue;
                    double lo = Get(o.Min, axis), hi = Get(o.Max, axis);
                    Try(lo, SnapKind.Edge, j);
                    Try(hi, SnapKind.Edge, j);
                    Try((lo + hi) / 2, SnapKind.Centre, j);
                }
            }
            if (guide == null) face = Grid(proposedFace, grid);

            double min = Get(box.Min, axis), max = Get(box.Max, axis);
            if (highFace) max = Math.Max(face, min + MinSide);
            else min = Math.Min(face, max - MinSide);
            if (guide != null && Math.Abs((highFace ? max : min) - face) > 1e-9) guide = null;   // held back by the minimum size
            return (new Bounds3(With(box.Min, axis, min), With(box.Max, axis, max)), guide);
        }
    }
}
