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
    public sealed record PlacedPart(Part Part, Vec3 Min, Vec3 Size, bool Rotated);

    public sealed record PrintPlate(int Number, IReadOnlyList<PlacedPart> Parts);

    public sealed record PrintPlan(PrintSettings Settings, IReadOnlyList<PrintPlate> Plates, IReadOnlyList<Part> TooBig)
    {
        public int PlacedCount => Plates.Sum(p => p.Parts.Count);
    }

    public static class PieceTransform
    {
        /// <summary>The same piece at another size (positions and sizes multiplied).</summary>
        public static PieceModel Scale(PieceModel model, double factor) =>
            new(model.Name, model.Parts.Select(p => new Part(p.Name, p.Min * factor, p.Size * factor, p.Kind)).ToList());
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
            new(name, plate.Parts.Select(p => new Part(p.Part.Name, p.Min, p.Size, p.Part.Kind)).ToList());

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
                            Put(flat, row.X, row.Z, w, d, rotated);
                            row.X += w + s.GapMm;
                            return true;
                        }
                    }

                    double z = _rows.Count == 0 ? edge : _rows[^1].Z + _rows[^1].Depth + s.GapMm;
                    if (z + d <= s.BedY - edge + 1e-9)
                    {
                        var row = new Row { Z = z, Depth = d, X = edge };
                        _rows.Add(row);
                        Put(flat, row.X, row.Z, w, d, rotated);
                        row.X += w + s.GapMm;
                        return true;
                    }
                }
                return false;
            }

            private void Put(Flat flat, double x, double z, double w, double d, bool rotated) =>
                Placed.Add(new PlacedPart(flat.Part, new Vec3(x, 0, z), new Vec3(w, flat.Height, d), rotated));
        }
    }
}
