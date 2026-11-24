using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// A piece described by dimension values (sliders). Whatever the sliders say, <see cref="Build"/> returns a valid piece:
    /// values outside a slider's range are pulled back, whole-number sliders are rounded, and sizes that depend on each
    /// other (a leg cannot be wider than a quarter of the table) are limited so the boards never overlap or float.
    /// </summary>
    public sealed class PieceTemplate
    {
        private readonly Func<IReadOnlyDictionary<string, double>, IReadOnlyList<Part>> _parts;

        public PieceTemplate(string id, string name, IReadOnlyList<ParamSpec> parameters, Func<IReadOnlyDictionary<string, double>, IReadOnlyList<Part>> parts)
        {
            Id = id;
            Name = name;
            Params = parameters;
            _parts = parts;
        }

        public string Id { get; }

        /// <summary>English fallback; the view looks the name up as Str_Modeler_Template_&lt;id&gt;.</summary>
        public string Name { get; }

        public IReadOnlyList<ParamSpec> Params { get; }

        /// <summary>The sliders as given, each missing one at its default and each one inside its range.</summary>
        public IReadOnlyDictionary<string, double> Resolve(IReadOnlyDictionary<string, double> values)
        {
            var resolved = new Dictionary<string, double>();
            foreach (var spec in Params)
            {
                double v = values.TryGetValue(spec.Key, out var given) && !double.IsNaN(given) ? given : spec.Default;
                if (spec.Integer) v = Math.Round(v, MidpointRounding.AwayFromZero);
                resolved[spec.Key] = Math.Clamp(v, spec.Min, spec.Max);
            }
            return resolved;
        }

        public PieceModel Build(IReadOnlyDictionary<string, double> values) => new(Name, _parts(Resolve(values)));
    }

    public static class PieceTemplates
    {
        public static IReadOnlyList<PieceTemplate> All { get; } = new[] { Table(), Chair(), Bookshelf(), Cabinet() };

        public static PieceTemplate? Find(string? id) => All.FirstOrDefault(t => t.Id == id);

        private static Part P(string name, double x, double y, double z, double sx, double sy, double sz, PartKind kind) =>
            new(name, new Vec3(x, y, z), new Vec3(sx, sy, sz), kind);

        // ----------------------------------------------------------------- table
        private static PieceTemplate Table() => new("table", "Table",
            new[]
            {
                new ParamSpec("width", "Width", 1200, 400, 3000, 10),
                new ParamSpec("depth", "Depth", 700, 300, 1500, 10),
                new ParamSpec("height", "Height", 750, 300, 1200, 10),
                new ParamSpec("top", "Top thickness", 30, 10, 80, 1),
                new ParamSpec("leg", "Leg size", 60, 20, 150, 1),
                new ParamSpec("apron", "Apron height", 80, 0, 200, 5),
                new ParamSpec("inset", "Leg inset", 30, 0, 200, 5),
            },
            v =>
            {
                double w = v["width"], d = v["depth"], h = v["height"];
                double m = Math.Min(w, d);
                double leg = Math.Min(v["leg"], m / 6);
                double inset = Math.Min(v["inset"], m / 8);
                double top = Math.Min(v["top"], h / 4);
                double legH = h - top;
                double apron = Math.Min(v["apron"], legH / 2);
                double apronT = Math.Min(leg / 2, 25);

                var parts = new List<Part> { P("Top", 0, legH, 0, w, top, d, PartKind.Top) };
                double xr = w - inset - leg, zf = d - inset - leg;
                parts.Add(P("Leg 1", inset, 0, inset, leg, legH, leg, PartKind.Leg));
                parts.Add(P("Leg 2", xr, 0, inset, leg, legH, leg, PartKind.Leg));
                parts.Add(P("Leg 3", inset, 0, zf, leg, legH, leg, PartKind.Leg));
                parts.Add(P("Leg 4", xr, 0, zf, leg, legH, leg, PartKind.Leg));

                if (apron >= 10)
                {
                    double along = w - 2 * (inset + leg);       // between the legs, left to right
                    double across = d - 2 * (inset + leg);      // between the legs, front to back
                    double centre = (leg - apronT) / 2;         // the apron is centred on the leg
                    double y = legH - apron;
                    parts.Add(P("Apron Back", inset + leg, y, inset + centre, along, apron, apronT, PartKind.Board));
                    parts.Add(P("Apron Front", inset + leg, y, zf + centre, along, apron, apronT, PartKind.Board));
                    parts.Add(P("Apron Left", inset + centre, y, inset + leg, apronT, apron, across, PartKind.Board));
                    parts.Add(P("Apron Right", xr + centre, y, inset + leg, apronT, apron, across, PartKind.Board));
                }
                return parts;
            });

        // ----------------------------------------------------------------- chair
        private static PieceTemplate Chair() => new("chair", "Chair",
            new[]
            {
                new ParamSpec("width", "Seat width", 420, 300, 600, 5),
                new ParamSpec("depth", "Seat depth", 420, 300, 600, 5),
                new ParamSpec("seatHeight", "Seat height", 450, 300, 600, 5),
                new ParamSpec("seat", "Seat thickness", 25, 15, 50, 1),
                new ParamSpec("backHeight", "Back height", 450, 200, 800, 10),
                new ParamSpec("leg", "Leg size", 40, 25, 80, 1),
            },
            v =>
            {
                double w = v["width"], d = v["depth"], sh = v["seatHeight"], bh = v["backHeight"];
                double leg = Math.Min(v["leg"], Math.Min(w, d) / 5);
                double seat = Math.Min(v["seat"], sh / 4);
                double legH = sh - seat;
                double slat = Math.Min(120, bh / 2);
                double slatT = Math.Min(20, leg / 2);

                return new[]
                {
                    P("Seat", 0, legH, 0, w, seat, d, PartKind.Top),
                    P("Leg 1", 0, 0, d - leg, leg, legH, leg, PartKind.Leg),
                    P("Leg 2", w - leg, 0, d - leg, leg, legH, leg, PartKind.Leg),
                    P("Leg 3", 0, 0, 0, leg, legH, leg, PartKind.Leg),
                    P("Leg 4", w - leg, 0, 0, leg, legH, leg, PartKind.Leg),
                    P("Back Post 1", 0, sh, 0, leg, bh, leg, PartKind.Leg),
                    P("Back Post 2", w - leg, sh, 0, leg, bh, leg, PartKind.Leg),
                    P("Back Slat", leg, sh + bh - slat, (leg - slatT) / 2, w - 2 * leg, slat, slatT, PartKind.Panel),
                };
            });

        // ------------------------------------------------------------- shelf carcass
        private const double BackThickness = 6;

        /// <summary>
        /// The box shared by the bookshelf and the cabinet: two sides, a bottom lifted by the base height, a top, evenly spaced
        /// shelves and a thin back. Returns the parts and the carcass depth used.
        /// </summary>
        private static List<Part> Carcass(double w, double h, double d, double thickness, int shelves, double baseHeight)
        {
            double t = Math.Min(thickness, Math.Min(w / 8, h / 20));
            double b = BackThickness;
            double lift = Math.Min(baseHeight, h / 6);
            double iw = w - 2 * t;
            double ih = h - 2 * t - lift;                                      // clear height between the bottom board and the top
            int fit = Math.Max(0, (int)Math.Floor((ih - 20) / (t + 20)));      // keep at least 20 mm between boards
            int n = Math.Min(shelves, fit);
            double gap = (ih - n * t) / (n + 1);

            var parts = new List<Part>
            {
                P("Side Left", 0, 0, 0, t, h, d, PartKind.Panel),
                P("Side Right", w - t, 0, 0, t, h, d, PartKind.Panel),
                P("Bottom", t, lift, b, iw, t, d - b, PartKind.Board),
                P("Top", t, h - t, b, iw, t, d - b, PartKind.Top),
                P("Back", t, lift + t, 0, iw, ih, b, PartKind.Panel),
            };
            for (int k = 1; k <= n; k++)
                parts.Add(P($"Shelf {k}", t, lift + t + k * gap + (k - 1) * t, b, iw, t, d - b, PartKind.Board));
            return parts;
        }

        // ------------------------------------------------------------- bookshelf
        private static PieceTemplate Bookshelf() => new("bookshelf", "Bookshelf",
            new[]
            {
                new ParamSpec("width", "Width", 800, 300, 2000, 10),
                new ParamSpec("height", "Height", 1800, 300, 2400, 10),
                new ParamSpec("depth", "Depth", 300, 150, 600, 5),
                new ParamSpec("thickness", "Board thickness", 18, 10, 40, 1),
                new ParamSpec("shelves", "Shelves", 4, 0, 10, 1, Integer: true),
                new ParamSpec("base", "Base height", 60, 0, 150, 5),
            },
            v => Carcass(v["width"], v["height"], v["depth"], v["thickness"], (int)v["shelves"], v["base"]));

        // --------------------------------------------------------------- cabinet
        private static PieceTemplate Cabinet() => new("cabinet", "Cabinet",
            new[]
            {
                new ParamSpec("width", "Width", 800, 400, 1600, 10),
                new ParamSpec("height", "Height", 800, 400, 1200, 10),
                new ParamSpec("depth", "Depth", 450, 250, 700, 5),
                new ParamSpec("thickness", "Board thickness", 18, 12, 30, 1),
                new ParamSpec("doors", "Doors", 1, 1, 2, 1, Integer: true),
                new ParamSpec("shelves", "Shelves", 1, 0, 5, 1, Integer: true),
                new ParamSpec("base", "Base height", 80, 0, 150, 5),
            },
            v =>
            {
                double w = v["width"], h = v["height"], d = v["depth"];
                double t = Math.Min(v["thickness"], Math.Min(w / 10, h / 20));
                double lift = Math.Min(v["base"], h / 6);
                int doors = (int)v["doors"];

                var parts = Carcass(w, h, d - t, v["thickness"], (int)v["shelves"], v["base"]);
                const double gap = 2;
                double doorW = (w - (doors - 1) * gap) / doors;
                for (int i = 0; i < doors; i++)
                    parts.Add(P($"Door {i + 1}", i * (doorW + gap), lift, d - t, doorW, h - lift, t, PartKind.Panel));
                return parts;
            });
    }
}
