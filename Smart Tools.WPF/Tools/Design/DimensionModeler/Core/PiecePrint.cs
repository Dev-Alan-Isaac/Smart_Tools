using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// How the piece is turned into something a printer can make. <see cref="ScaleRatio"/> 10 means 1:10; the clearance is the
    /// gap left between parts that meet (each part is made that much smaller all round, so two parts end up a clearance apart);
    /// the bed is the print area in X and Y and the tallest thing it can print in Z.
    /// </summary>
    public sealed record PrintSettings(double ScaleRatio, double ClearanceMm, double BedX, double BedY, double BedHeight, double GapMm)
    {
        /// <summary>The multiplier: 1 for full size, 0.1 for 1:10. A ratio below 1 is taken as full size.</summary>
        public double Scale => ScaleRatio < 1 ? 1 : 1.0 / ScaleRatio;

        public static PrintSettings Default { get; } = new(1, 0.2, 220, 220, 250, 4);
    }

    /// <summary>A part lying flat on a plate: <see cref="Min"/>/<see cref="Size"/> are in plate coordinates (X, Z on the bed, Y up).</summary>
    public sealed record PlacedPart(Part Part, Vec3 Min, Vec3 Size, bool Rotated, IReadOnlyList<Hole>? Holes = null, IReadOnlyList<Bevel>? Bevels = null);

    public sealed record PrintPlate(int Number, IReadOnlyList<PlacedPart> Parts);

    public sealed record PrintPlan(PrintSettings Settings, IReadOnlyList<PrintPlate> Plates, IReadOnlyList<Part> TooBig)
    {
        public int PlacedCount => Plates.Sum(p => p.Parts.Count);
    }

    public static class PieceTransform
    {
        /// <summary>The same piece at another size (positions and sizes multiplied).</summary>
        public static PieceModel Scale(PieceModel model, double factor) =>
            new(model.Name, model.Parts.Select(p => new Part(p.Name, p.Min * factor, p.Size * factor, p.Kind,
                p.Holes.Select(h => new Hole(h.Axis, h.U * factor, h.V * factor, h.Diameter * factor)).ToList(),
                p.Bevels.Select(b => b with { Depth = b.Depth * factor }).ToList(), p.Group)).ToList());   // an angle stays the same at any size
    }

    public static class PrintPlanner
    {
        private const double MinFeature = 0.2;   // a part never shrinks below this in any direction

        private sealed record Flat(Part Part, double Height, double Long, double Short);

        /// <summary>Scales every part, applies the clearance, lays each flat with its thinnest side up, and packs them onto plates.</summary>
        public static PrintPlan Plan(PieceModel model, PrintSettings settings)
        {
            var flats = new List<Flat>();
            var tooBig = new List<Part>();
            double usableX = settings.BedX - settings.GapMm;
            double usableY = settings.BedY - settings.GapMm;

            foreach (var part in model.Parts)
            {
                var dims = new[] { part.Size.X, part.Size.Y, part.Size.Z }
                    .Select(d => Math.Max(MinFeature, d * settings.Scale - settings.ClearanceMm))
                    .OrderBy(d => d).ToArray();
                var flat = new Flat(part, dims[0], dims[2], dims[1]);
                bool fits = flat.Height <= settings.BedHeight
                            && (flat.Long <= usableX && flat.Short <= usableY || flat.Short <= usableX && flat.Long <= usableY);
                if (fits) flats.Add(flat);
                else tooBig.Add(part);
            }

            var ordered = flats.OrderByDescending(f => f.Short).ThenByDescending(f => f.Long).ThenBy(f => f.Part.Name, StringComparer.Ordinal).ToList();
            var plates = new List<PlateBuilder>();
            foreach (var flat in ordered)
            {
                if (!plates.Any(p => p.TryPlace(flat, settings)))
                {
                    var plate = new PlateBuilder(plates.Count + 1);
                    plate.TryPlace(flat, settings);
                    plates.Add(plate);
                }
            }
            return new PrintPlan(settings, plates.Select(p => new PrintPlate(p.Number, p.Placed)).ToList(), tooBig);
        }

        /// <summary>One plate as a model of its own, ready to be exported.</summary>
        public static PieceModel PlateModel(PrintPlate plate, string name) =>
            new(name, plate.Parts.Select(p => new Part(p.Part.Name, p.Min, p.Size, p.Part.Kind, p.Holes, p.Bevels)).ToList());

        /// <summary>
        /// Where a board's holes end up when it lies flat: scaled, moved in by half the clearance, and turned with the board (a true
        /// turn, so a knob hole stays on the same side). Only holes through the board's thinnest direction come along: the others
        /// would not go through a board lying on its side.
        /// </summary>
        private static IReadOnlyList<Hole>? FlatHoles(Part part, PrintSettings s, bool rotated, double width, double depth)
        {
            if (part.Holes.Count == 0) return null;
            int thin = PieceHoles.ThinAxis(part.Size);
            if (part.Holes[0].Axis != thin) return null;
            var (a1, a2) = PieceHoles.Across(thin);
            double Dim(int axis) => Math.Max(MinFeature, (axis == 0 ? part.Size.X : axis == 1 ? part.Size.Y : part.Size.Z) * s.Scale - s.ClearanceMm);
            double l1 = Dim(a1), l2 = Dim(a2);
            bool xAlongFirst = (l1 >= l2) != rotated;
            var flat = part.Holes.Select(h =>
            {
                double u = h.U * s.Scale - s.ClearanceMm / 2, v = h.V * s.Scale - s.ClearanceMm / 2;
                return xAlongFirst ? new Hole(1, u, v, h.Diameter * s.Scale) : new Hole(1, v, l1 - u, h.Diameter * s.Scale);
            });
            return PieceHoles.Sanitize(new Vec3(width, 1, depth), flat);
        }

        /// <summary>
        /// A board's bevels as it lies on the plate: each face is turned the same way as the holes (thinnest side up, then the same quarter
        /// turn), and the depth is scaled. The angle stays.
        /// </summary>
        private static IReadOnlyList<Bevel>? FlatBevels(Part part, PrintSettings s, bool rotated, double width, double height, double depth)
        {
            if (part.Bevels.Count == 0) return null;
            int thin = PieceHoles.ThinAxis(part.Size);
            var (a1, a2) = PieceHoles.Across(thin);
            double Dim(int axis) => Math.Max(MinFeature, (axis == 0 ? part.Size.X : axis == 1 ? part.Size.Y : part.Size.Z) * s.Scale - s.ClearanceMm);
            bool xAlongFirst = (Dim(a1) >= Dim(a2)) != rotated;
            // where each of the board's directions goes on the plate, and whether it is turned round
            var map = new (int Axis, bool Flip)[3];
            map[thin] = (1, false);
            if (xAlongFirst)
            {
                map[a1] = (0, false);
                map[a2] = (2, false);
            }
            else
            {
                map[a2] = (0, false);
                map[a1] = (2, true);
            }
            int Face(int face)
            {
                var (axis, flip) = map[PieceBevels.AxisOf(face)];
                return axis * 2 + (PieceBevels.IsHigh(face) != flip ? 1 : 0);
            }
            var flat = part.Bevels.Select(b => new Bevel(Face(b.FaceA), Face(b.FaceB), b.Angle, b.Depth * s.Scale));
            return PieceBevels.Sanitize(new Vec3(width, height, depth), flat);
        }

        /// <summary>Rows of parts across the bed; a part goes in the first row it fits, else a new row, else the next plate.</summary>
        private sealed class PlateBuilder
        {
            private sealed class Row
            {
                public double Z;
                public double Depth;
                public double X;
            }

            private readonly List<Row> _rows = new();

            public PlateBuilder(int number) => Number = number;

            public int Number { get; }

            public List<PlacedPart> Placed { get; } = new();

            public bool TryPlace(Flat flat, PrintSettings s)
            {
                double edge = s.GapMm / 2;
                // the long side along X first; turned a quarter if that does not fit
                foreach (bool rotated in new[] { false, true })
                {
                    double w = rotated ? flat.Short : flat.Long;
                    double d = rotated ? flat.Long : flat.Short;
                    if (w > s.BedX - s.GapMm || d > s.BedY - s.GapMm) continue;

                    foreach (var row in _rows)
                    {
                        if (d <= row.Depth + 1e-9 && row.X + w <= s.BedX - edge + 1e-9)
                        {
                            Put(flat, row.X, row.Z, w, d, rotated, s);
                            row.X += w + s.GapMm;
                            return true;
                        }
                    }

                    double z = _rows.Count == 0 ? edge : _rows[^1].Z + _rows[^1].Depth + s.GapMm;
                    if (z + d <= s.BedY - edge + 1e-9)
                    {
                        var row = new Row { Z = z, Depth = d, X = edge };
                        _rows.Add(row);
                        Put(flat, row.X, row.Z, w, d, rotated, s);
                        row.X += w + s.GapMm;
                        return true;
                    }
                }
                return false;
            }

            private void Put(Flat flat, double x, double z, double w, double d, bool rotated, PrintSettings s) =>
                Placed.Add(new PlacedPart(flat.Part, new Vec3(x, 0, z), new Vec3(w, flat.Height, d), rotated, FlatHoles(flat.Part, s, rotated, w, d), FlatBevels(flat.Part, s, rotated, w, flat.Height, d)));
        }
    }
}
