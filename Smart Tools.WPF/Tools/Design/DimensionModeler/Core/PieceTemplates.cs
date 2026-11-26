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
        public static IReadOnlyList<PieceTemplate> All { get; } = new[] { Table(), Chair(), Bookshelf(), Cabinet(), FilingCabinet(), Door(), Desk(), DeskL(), Shelf(), KitchenBase(), WallCabinet(), Wardrobe(), Sideboard() };

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
                new ParamSpec("bevel", "Top edge bevel", 0, 0, 15, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
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

                var parts = new List<Part> { EdgeBevels(P("Top", 0, legH, 0, w, top, d, PartKind.Top), FaceTop, new[] { FaceLeft, FaceRight, FaceFront, FaceBack }, v["bevel"], v["bevelAngle"]) };
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
                new ParamSpec("bevel", "Seat edge bevel", 0, 0, 10, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
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
                    EdgeBevels(P("Seat", 0, legH, 0, w, seat, d, PartKind.Top), FaceTop, new[] { FaceLeft, FaceRight, FaceFront }, v["bevel"], v["bevelAngle"]),
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
        private static List<Part> Carcass(double w, double h, double d, double thickness, int shelves, double baseHeight, bool miterTop = false)
        {
            double t = Math.Min(thickness, Math.Min(w / 8, h / 20));
            double b = BackThickness;
            double lift = Math.Min(baseHeight, h / 6);
            double iw = w - 2 * t;
            double ih = h - 2 * t - lift;                                      // clear height between the bottom board and the top
            int fit = Math.Max(0, (int)Math.Floor((ih - 20) / (t + 20)));      // keep at least 20 mm between boards
            int n = Math.Min(shelves, fit);
            double gap = (ih - n * t) / (n + 1);

            var sideLeft = P("Side Left", 0, 0, 0, t, h, d, PartKind.Panel);
            var sideRight = P("Side Right", w - t, 0, 0, t, h, d, PartKind.Panel);
            var top = P("Top", t, h - t, b, iw, t, d - b, PartKind.Top);
            if (miterTop)
            {
                // the top runs the full width and meets each side at 45 degrees: both boards are cut right through at the corner
                top = P("Top", 0, h - t, b, w, t, d - b, PartKind.Top);
                top = top with { Edges = PieceBevels.Sanitize(top.Size, new[] { new Bevel(FaceLeft, FaceBottom, 45, t), new Bevel(FaceRight, FaceBottom, 45, t) }) };
                sideLeft = sideLeft with { Edges = PieceBevels.Sanitize(sideLeft.Size, new[] { new Bevel(FaceTop, FaceRight, 45, t) }) };
                sideRight = sideRight with { Edges = PieceBevels.Sanitize(sideRight.Size, new[] { new Bevel(FaceTop, FaceLeft, 45, t) }) };
            }
            var parts = new List<Part>
            {
                sideLeft,
                sideRight,
                P("Bottom", t, lift, b, iw, t, d - b, PartKind.Board),
                top,
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
                new ParamSpec("miter", "Mitred top corners", 0, 0, 1, 1, Integer: true),
                new ParamSpec("bevel", "Shelf edge bevel", 0, 0, 10, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
            },
            v => Carcass(v["width"], v["height"], v["depth"], v["thickness"], (int)v["shelves"], v["base"], v["miter"] >= 0.5)
                .Select(p => p.Name.StartsWith("Shelf") ? EdgeBevels(p, FaceFront, new[] { FaceTop }, v["bevel"], v["bevelAngle"]) : p).ToList());

        // --------------------------------------------------------------- cabinet
        private static PieceTemplate Cabinet() => new("cabinet", "Cabinet",
            new[]
            {
                new ParamSpec("width", "Width", 800, 400, 1600, 10),
                new ParamSpec("height", "Height", 800, 400, 1200, 10),
                new ParamSpec("depth", "Depth", 450, 250, 700, 5),
                new ParamSpec("thickness", "Board thickness", 18, 12, 30, 1),
                new ParamSpec("doors", "Doors", 1, 0, 2, 1, Integer: true),
                new ParamSpec("knob", "Knob hole", 8, 0, 40, 1),
                new ParamSpec("shelves", "Shelves", 1, 0, 5, 1, Integer: true),
                new ParamSpec("base", "Base height", 80, 0, 150, 5),
                new ParamSpec("bevel", "Door edge bevel", 0, 0, 15, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
                new ParamSpec("miter", "Mitred top corners", 0, 0, 1, 1, Integer: true),
            },
            v =>
            {
                double w = v["width"], h = v["height"], d = v["depth"];
                double t = Math.Min(v["thickness"], Math.Min(w / 10, h / 20));
                double lift = Math.Min(v["base"], h / 6);
                int doors = (int)v["doors"];

                var parts = Carcass(w, h, d - t, v["thickness"], (int)v["shelves"], v["base"], v["miter"] >= 0.5);
                double doorH = h - lift;
                parts.AddRange(Doors(doors, 0, lift, w, doorH, d - t, t, v["knob"], doorH / 2, v["bevel"], v["bevelAngle"]));
                return parts;
            });

        private const double FrontGap = 2;          // between two doors or drawer fronts
        private const double KnobFromEdge = 40;     // a door's knob hole sits this far from the edge opposite the hinge

        /// <summary>
        /// One door on the front: a panel in its own group ("Door k"), bevelled edges, and a knob hole at height
        /// <paramref name="knobV"/> on the side away from the hinge.
        /// </summary>
        private static Part OneDoor(int k, double x, double y, double width, double height, double z, double t, bool knobOnRight, double knob, double knobV, double bevel, double angle)
        {
            var door = FrontBevels(P($"Door {k}", x, y, z, width, height, t, PartKind.Panel), bevel, angle) with { Group = $"Door {k}" };
            double u = knobOnRight ? width - KnobFromEdge : KnobFromEdge;
            return WithHole(door, new Hole(2, u, Math.Clamp(knobV, 0, height), knob));   // punched through the door's thickness (Z)
        }

        /// <summary>
        /// <paramref name="count"/> doors side by side across a width: the first is hinged on the left (knob on the right),
        /// the last of two or more is its mirror, any between open like the first.
        /// </summary>
        private static IEnumerable<Part> Doors(int count, double x, double y, double width, double height, double z, double t, double knob, double knobV, double bevel, double angle)
        {
            if (count <= 0) yield break;
            double doorW = (width - (count - 1) * FrontGap) / count;
            for (int i = 0; i < count; i++)
                yield return OneDoor(i + 1, x + i * (doorW + FrontGap), y, doorW, height, z, t, !(count > 1 && i == count - 1), knob, knobV, bevel, angle);
        }

        /// <summary>
        /// A drawer as one group: the front (with a handle hole) and, behind it, a box of bottom, two sides and back that
        /// rides between <paramref name="innerX0"/> and <paramref name="innerX1"/> from <paramref name="boxY0"/> to
        /// <paramref name="boxY1"/>, from just in front of the carcass back to the front.
        /// </summary>
        private static IEnumerable<Part> Drawer(string group, double frontX, double frontY, double frontW, double frontH, double z, double t,
            double innerX0, double innerX1, double boxY0, double boxY1, double handle, double bevel, double angle)
        {
            const double slide = 12;                                            // room for the runners
            double side = Math.Min(12, t);
            double x0 = innerX0 + slide, x1 = innerX1 - slide, zb = BackThickness + 10;
            var front = FrontBevels(P($"{group} Front", frontX, frontY, z, frontW, frontH, t, PartKind.Panel), bevel, angle);
            yield return WithHole(front, new Hole(2, frontW / 2, frontH * 0.7, handle)) with { Group = group };
            yield return P($"{group} Bottom", x0 + side, boxY0, zb, x1 - x0 - 2 * side, side, z - zb, PartKind.Board) with { Group = group };
            yield return P($"{group} Side Left", x0, boxY0, zb, side, boxY1 - boxY0, z - zb, PartKind.Board) with { Group = group };
            yield return P($"{group} Side Right", x1 - side, boxY0, zb, side, boxY1 - boxY0, z - zb, PartKind.Board) with { Group = group };
            yield return P($"{group} Back", x0 + side, boxY0 + side, zb, x1 - x0 - 2 * side, boxY1 - boxY0 - side, side, PartKind.Board) with { Group = group };
        }

        /// <summary>Up to <paramref name="count"/> boards evenly spaced between two heights, fewer when they would sit closer than 20 mm.</summary>
        private static IEnumerable<Part> EvenShelves(string name, int count, double x, double width, double y0, double y1, double z, double depth, double t)
        {
            int fit = Math.Max(0, (int)Math.Floor((y1 - y0 - 20) / (t + 20)));
            int n = Math.Min(count, fit);
            double gap = (y1 - y0 - n * t) / (n + 1);
            for (int k = 1; k <= n; k++)
                yield return P(n > 1 ? $"{name} {k}" : name, x, y0 + k * gap + (k - 1) * t, z, width, t, depth, PartKind.Board);
        }

        // faces for bevels: 0 left, 1 right, 2 bottom, 3 top, 4 back, 5 front
        private const int FaceLeft = 0, FaceRight = 1, FaceBottom = 2, FaceTop = 3, FaceBack = 4, FaceFront = 5;

        /// <summary>Some edges of one face cut at an angle (the top edges of a tabletop, for example). Size 0 = square edges.</summary>
        private static Part EdgeBevels(Part part, int face, IEnumerable<int> towards, double size, double angle)
        {
            if (size < 0.5) return part;
            var kept = PieceBevels.Sanitize(part.Size, part.Bevels.Concat(towards.Select(f => new Bevel(face, f, angle, size))));
            return kept.Count == 0 ? part : part with { Edges = kept };
        }

        /// <summary>All four edges of a board's front face cut at an angle (a bevelled door or drawer front). Size 0 = square edges.</summary>
        private static Part FrontBevels(Part part, double size, double angle)
        {
            if (size < 0.5) return part;
            var bevels = new[] { FaceLeft, FaceRight, FaceTop, FaceBottom }.Select(f => new Bevel(FaceFront, f, angle, size));
            var kept = PieceBevels.Sanitize(part.Size, bevels);
            return kept.Count == 0 ? part : part with { Edges = kept };
        }

        /// <summary>A round hole through a board, or the board unchanged when the diameter is too small to cut.</summary>
        private static Part WithHole(Part part, Hole hole) =>
            hole.Diameter >= PieceHoles.MinDiameter
                ? part with { Cuts = PieceHoles.Sanitize(part.Size, new[] { hole }).Where(h => PieceBevels.HoleIsClear(part.Size, h, part.Bevels)).ToList() }
                : part;

        // --------------------------------------------------------- filing cabinet
        private static PieceTemplate FilingCabinet() => new("filing-cabinet", "Filing cabinet",
            new[]
            {
                new ParamSpec("width", "Width", 470, 400, 1000, 10),
                new ParamSpec("height", "Height", 1320, 500, 1400, 10),
                new ParamSpec("depth", "Depth", 620, 400, 800, 5),
                new ParamSpec("thickness", "Board thickness", 18, 12, 30, 1),
                new ParamSpec("drawers", "Drawers", 4, 1, 5, 1, Integer: true),
                new ParamSpec("handle", "Handle hole", 12, 0, 40, 1),
                new ParamSpec("base", "Base height", 50, 0, 150, 5),
                new ParamSpec("bevel", "Front edge bevel", 0, 0, 10, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
                new ParamSpec("miter", "Mitred top corners", 0, 0, 1, 1, Integer: true),
            },
            v =>
            {
                double w = v["width"], h = v["height"], d = v["depth"];
                double t = Math.Min(v["thickness"], Math.Min(w / 8, h / 20));   // the same limits the carcass uses
                double lift = Math.Min(v["base"], h / 6);
                double dc = d - t;                                              // the carcass; the drawer fronts sit in front of it
                int n = (int)v["drawers"];
                var parts = Carcass(w, h, dc, v["thickness"], 0, v["base"], v["miter"] >= 0.5);

                double ih = h - 2 * t - lift;
                double slot = ih / n;
                double frontH = (h - lift - (n - 1) * FrontGap) / n;
                for (int i = 0; i < n; i++)
                {
                    // front and box move, hide and pull out together; the box rides between the sides and touches the front
                    double y0 = lift + t + i * slot + 5, y1 = lift + t + (i + 1) * slot - 5;
                    parts.AddRange(Drawer($"Drawer {i + 1}", 0, lift + i * (frontH + FrontGap), w, frontH, dc, t, t, w - t, y0, y1, v["handle"], v["bevel"], v["bevelAngle"]));
                }
                return parts;
            });

        // ------------------------------------------------------ kitchen base cabinet
        private static PieceTemplate KitchenBase() => new("kitchen-base", "Kitchen base cabinet",
            new[]
            {
                new ParamSpec("width", "Width", 600, 300, 1200, 10),
                new ParamSpec("height", "Height", 870, 700, 950, 5),
                new ParamSpec("depth", "Depth", 580, 450, 650, 5),
                new ParamSpec("thickness", "Board thickness", 18, 15, 25, 1),
                new ParamSpec("drawer", "Drawer height", 150, 0, 250, 5),
                new ParamSpec("doors", "Doors", 2, 1, 2, 1, Integer: true),
                new ParamSpec("shelves", "Shelves", 1, 0, 2, 1, Integer: true),
                new ParamSpec("knob", "Knob hole", 8, 0, 40, 1),
                new ParamSpec("base", "Plinth height", 100, 0, 150, 5),
                new ParamSpec("bevel", "Front edge bevel", 0, 0, 10, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
            },
            v =>
            {
                double w = v["width"], h = v["height"], d = v["depth"];
                double t = Math.Min(v["thickness"], Math.Min(w / 8, h / 20));   // the same limits the carcass uses
                double lift = Math.Min(v["base"], h / 6);
                double dc = d - t;                                              // the fronts sit in front of the carcass
                var parts = Carcass(w, h, dc, v["thickness"], 0, v["base"]);

                // a drawer at the top (under the worktop), the doors below it
                double dh = Math.Min(v["drawer"], (h - lift) / 3);
                bool drawer = dh >= 80;
                double doorTop = drawer ? h - dh - FrontGap : h;
                double ceiling = h - t;                                         // the lowest board above the cupboard space
                if (drawer)
                {
                    double rail = h - dh - FrontGap / 2 - t / 2;                // the board between drawer and cupboard
                    parts.Add(P("Rail", t, rail, BackThickness, w - 2 * t, t, dc - BackThickness, PartKind.Board));
                    parts.AddRange(Drawer("Drawer", 0, h - dh, w, dh, dc, t, t, w - t, rail + t + 5, h - t - 5, v["knob"], v["bevel"], v["bevelAngle"]));
                    ceiling = rail;
                }
                parts.AddRange(EvenShelves("Shelf", (int)v["shelves"], t, w - 2 * t, lift + t, ceiling, BackThickness, dc - BackThickness, t));
                double doorH = doorTop - lift;
                parts.AddRange(Doors((int)v["doors"], 0, lift, w, doorH, dc, t, v["knob"], doorH - 60, v["bevel"], v["bevelAngle"]));
                return parts;
            });

        // ------------------------------------------------------------ wall cabinet
        private static PieceTemplate WallCabinet() => new("wall-cabinet", "Wall cabinet",
            new[]
            {
                new ParamSpec("width", "Width", 600, 300, 1200, 10),
                new ParamSpec("height", "Height", 700, 300, 1000, 10),
                new ParamSpec("depth", "Depth", 330, 250, 400, 5),
                new ParamSpec("thickness", "Board thickness", 18, 15, 25, 1),
                new ParamSpec("doors", "Doors", 2, 0, 2, 1, Integer: true),
                new ParamSpec("shelves", "Shelves", 2, 0, 4, 1, Integer: true),
                new ParamSpec("knob", "Knob hole", 8, 0, 40, 1),
                new ParamSpec("bevel", "Door edge bevel", 0, 0, 10, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
                new ParamSpec("miter", "Mitred top corners", 0, 0, 1, 1, Integer: true),
            },
            v =>
            {
                double w = v["width"], h = v["height"], d = v["depth"];
                double t = Math.Min(v["thickness"], Math.Min(w / 8, h / 20));
                var parts = Carcass(w, h, d - t, v["thickness"], (int)v["shelves"], 0, v["miter"] >= 0.5);
                // it hangs above your head, so the knob holes are near the bottom of the doors
                parts.AddRange(Doors((int)v["doors"], 0, 0, w, h, d - t, t, v["knob"], 60, v["bevel"], v["bevelAngle"]));
                return parts;
            });

        // ---------------------------------------------------------------- wardrobe
        private static PieceTemplate Wardrobe() => new("wardrobe", "Wardrobe",
            new[]
            {
                new ParamSpec("width", "Width", 1000, 600, 2000, 10),
                new ParamSpec("height", "Height", 2000, 1600, 2400, 10),
                new ParamSpec("depth", "Depth", 600, 500, 700, 5),
                new ParamSpec("thickness", "Board thickness", 18, 15, 25, 1),
                new ParamSpec("doors", "Doors", 2, 1, 3, 1, Integer: true),
                new ParamSpec("drawers", "Drawers", 1, 0, 2, 1, Integer: true),
                new ParamSpec("knob", "Knob hole", 8, 0, 40, 1),
                new ParamSpec("base", "Base height", 80, 0, 150, 5),
                new ParamSpec("bevel", "Front edge bevel", 0, 0, 10, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
                new ParamSpec("miter", "Mitred top corners", 0, 0, 1, 1, Integer: true),
            },
            v =>
            {
                double w = v["width"], h = v["height"], d = v["depth"];
                double t = Math.Min(v["thickness"], Math.Min(w / 8, h / 20));
                double lift = Math.Min(v["base"], h / 6);
                double dc = d - t;
                int n = (int)v["drawers"];
                const double drawerH = 200;
                double iw = w - 2 * t, zd = dc - BackThickness;
                var parts = Carcass(w, h, dc, v["thickness"], 0, v["base"], v["miter"] >= 0.5);

                // drawers at the bottom with a board over them; the doors cover the hanging space above
                double doorY = lift + n * (drawerH + FrontGap);
                if (n > 0)
                {
                    double divider = doorY - FrontGap / 2 - t / 2;
                    parts.Add(P("Divider", t, divider, BackThickness, iw, t, zd, PartKind.Board));
                    double slot = (divider - lift - t) / n;
                    for (int i = 0; i < n; i++)
                        parts.AddRange(Drawer($"Drawer {i + 1}", 0, lift + i * (drawerH + FrontGap), w, drawerH, dc, t, t, w - t,
                            lift + t + i * slot + 5, lift + t + (i + 1) * slot - 5, v["knob"], v["bevel"], v["bevelAngle"]));
                }

                // a hat shelf under the top and the hanging rail under that, both from side to side
                double hat = h - 2 * t - 250;
                parts.Add(P("Hat Shelf", t, hat, BackThickness, iw, t, zd, PartKind.Board));
                const double railSize = 25;
                parts.Add(P("Hanging Rail", t, hat - 80 - railSize, BackThickness + (zd - railSize) / 2, iw, railSize, railSize, PartKind.Leg));

                double doorH = h - doorY;
                parts.AddRange(Doors((int)v["doors"], 0, doorY, w, doorH, dc, t, v["knob"], doorH / 2, v["bevel"], v["bevelAngle"]));
                return parts;
            });

        // ------------------------------------------------------ sideboard / TV unit
        private static PieceTemplate Sideboard() => new("sideboard", "Sideboard / TV unit",
            new[]
            {
                new ParamSpec("width", "Width", 1600, 900, 2400, 10),
                new ParamSpec("height", "Height", 600, 400, 900, 10),
                new ParamSpec("depth", "Depth", 450, 350, 550, 5),
                new ParamSpec("thickness", "Board thickness", 18, 15, 25, 1),
                new ParamSpec("drawers", "Middle drawers", 2, 0, 3, 1, Integer: true),
                new ParamSpec("knob", "Knob hole", 8, 0, 40, 1),
                new ParamSpec("base", "Base height", 80, 0, 150, 5),
                new ParamSpec("bevel", "Front edge bevel", 0, 0, 10, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
                new ParamSpec("miter", "Mitred top corners", 0, 0, 1, 1, Integer: true),
            },
            v =>
            {
                double w = v["width"], h = v["height"], d = v["depth"];
                double t = Math.Min(v["thickness"], Math.Min(w / 8, h / 20));
                double lift = Math.Min(v["base"], h / 6);
                double dc = d - t, zd = dc - BackThickness;
                int n = (int)v["drawers"];
                var parts = Carcass(w, h, dc, v["thickness"], 0, v["base"], v["miter"] >= 0.5);

                // three bays split by two upright boards: a door on each end, drawers (or an open shelf) in the middle
                double inner = h - 2 * t - lift;
                double bay = (w - 4 * t) / 3;
                double x1 = t + bay, x2 = x1 + t + bay;                          // left edge of each divider
                parts.Add(P("Divider Left", x1, lift + t, BackThickness, t, inner, zd, PartKind.Panel));
                parts.Add(P("Divider Right", x2, lift + t, BackThickness, t, inner, zd, PartKind.Panel));

                double frontH = h - lift;
                double split1 = x1 + t / 2, split2 = x2 + t / 2;                 // fronts meet over the middle of a divider
                double half = FrontGap / 2;
                parts.Add(OneDoor(1, 0, lift, split1 - half, frontH, dc, t, true, v["knob"], frontH / 2, v["bevel"], v["bevelAngle"]));
                parts.Add(OneDoor(2, split2 + half, lift, w - split2 - half, frontH, dc, t, false, v["knob"], frontH / 2, v["bevel"], v["bevelAngle"]));

                double mx = split1 + half, mw = split2 - half - mx;
                if (n == 0)
                {
                    parts.AddRange(EvenShelves("Open Shelf", 1, x1 + t, bay, lift + t, h - t, BackThickness, zd, t));
                }
                else
                {
                    double dh = (frontH - (n - 1) * FrontGap) / n, slot = inner / n;
                    for (int i = 0; i < n; i++)
                        parts.AddRange(Drawer($"Drawer {i + 1}", mx, lift + i * (dh + FrontGap), mw, dh, dc, t, x1 + t, x2,
                            lift + t + i * slot + 5, lift + t + (i + 1) * slot - 5, v["knob"], v["bevel"], v["bevelAngle"]));
                }
                return parts;
            });

        // ------------------------------------------------------------------- door
        private static PieceTemplate Door() => new("door", "Door",
            new[]
            {
                new ParamSpec("width", "Width with frame", 900, 600, 1200, 10),
                new ParamSpec("height", "Height with frame", 2100, 1800, 2400, 10),
                new ParamSpec("thickness", "Door thickness", 40, 30, 60, 1),
                new ParamSpec("frame", "Frame width", 30, 20, 60, 1),
                new ParamSpec("frameDepth", "Frame depth", 120, 80, 200, 5),
                new ParamSpec("knob", "Lock hole", 54, 0, 60, 1),
                new ParamSpec("knobHeight", "Lock height", 1000, 800, 1200, 10),
                new ParamSpec("bevel", "Door edge bevel", 0, 0, 15, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
            },
            v =>
            {
                double w = v["width"], h = v["height"], f = v["frame"], fd = v["frameDepth"];
                double dt = Math.Min(v["thickness"], fd - 10);
                const double gap = 3, undercut = 10;                       // around the door, and the gap over the floor
                double z0 = (fd - dt) / 2;
                double slabX = f + gap, slabW = w - 2 * f - 2 * gap;
                double slabY = undercut, slabH = h - f - gap - undercut;

                var parts = new List<Part>
                {
                    P("Jamb Left", 0, 0, 0, f, h - f, fd, PartKind.Board),
                    P("Jamb Right", w - f, 0, 0, f, h - f, fd, PartKind.Board),
                    P("Head", 0, h - f, 0, w, f, fd, PartKind.Board),
                };
                var slab = FrontBevels(P("Door", slabX, slabY, z0, slabW, slabH, dt, PartKind.Panel), v["bevel"], v["bevelAngle"]) with { Group = "Door" };
                double lockV = Math.Clamp(v["knobHeight"] - slabY, 100, slabH - 100);
                parts.Add(WithHole(slab, new Hole(2, slabW - 60, lockV, v["knob"])));   // the lock is on the side away from the hinges

                // three hinges fill the gap on the left, joining the door to its frame
                const double hingeH = 100;
                foreach (var (name, y) in new[] { ("Hinge 1", slabY + 200), ("Hinge 2", slabY + (slabH - hingeH) / 2), ("Hinge 3", slabY + slabH - 200 - hingeH) })
                    parts.Add(P(name, f, y, z0, gap, hingeH, Math.Min(25, dt), PartKind.Leg) with { Group = "Door" });
                return parts;
            });

        // ------------------------------------------------------------------- desk
        private static PieceTemplate Desk() => new("desk", "Desk",
            new[]
            {
                new ParamSpec("width", "Width", 1400, 800, 2400, 10),
                new ParamSpec("depth", "Depth", 700, 500, 900, 10),
                new ParamSpec("height", "Height", 740, 650, 800, 5),
                new ParamSpec("top", "Top thickness", 25, 18, 40, 1),
                new ParamSpec("thickness", "Side thickness", 25, 18, 40, 1),
                new ParamSpec("modesty", "Back panel height", 300, 0, 500, 10),
                new ParamSpec("cable", "Cable hole", 60, 0, 80, 1),
                new ParamSpec("bevel", "Front edge bevel", 0, 0, 15, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
            },
            v =>
            {
                double w = v["width"], d = v["depth"], h = v["height"], top = v["top"], t = v["thickness"];
                double legH = h - top;
                var deskTop = P("Top", 0, legH, 0, w, top, d, PartKind.Top);
                if (v["bevel"] >= 0.5)   // the front edge, where your wrists rest
                    deskTop = deskTop with { Edges = PieceBevels.Sanitize(deskTop.Size, new[] { new Bevel(FaceTop, FaceFront, v["bevelAngle"], v["bevel"]) }) };
                var parts = new List<Part>
                {
                    WithHole(deskTop, new Hole(1, w / 2, 80, v["cable"])),   // a cable hole near the back
                    P("Side Left", 0, 0, 0, t, legH, d, PartKind.Panel),
                    P("Side Right", w - t, 0, 0, t, legH, d, PartKind.Panel),
                };
                double mh = Math.Min(v["modesty"], legH - 100);
                if (mh >= 10) parts.Add(P("Back Panel", t, legH - mh, 50, w - 2 * t, mh, t, PartKind.Panel));
                return parts;
            });

        // ------------------------------------------------------------ L-shaped desk
        private static PieceTemplate DeskL() => new("desk-l", "Desk in L shape",
            new[]
            {
                new ParamSpec("width", "Main width", 1600, 1200, 2400, 10),
                new ParamSpec("depth", "Main depth", 700, 500, 900, 10),
                new ParamSpec("returnLength", "Side length", 1400, 1000, 2000, 10),
                new ParamSpec("returnDepth", "Side depth", 600, 400, 800, 10),
                new ParamSpec("height", "Height", 740, 650, 800, 5),
                new ParamSpec("top", "Top thickness", 25, 18, 40, 1),
                new ParamSpec("thickness", "Side thickness", 25, 18, 40, 1),
                new ParamSpec("cable", "Cable hole", 60, 0, 80, 1),
                new ParamSpec("bevel", "Front edge bevel", 0, 0, 15, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
            },
            v =>
            {
                double w = v["width"], d = v["depth"], h = v["height"], top = v["top"], t = v["thickness"];
                double rd = Math.Min(v["returnDepth"], w / 2);                   // the side wing, on the right, coming towards you
                double rl = Math.Max(v["returnLength"], d + 300);
                double legH = h - top;
                double back = Math.Min(300, legH - 100);
                return new[]
                {
                    WithHole(EdgeBevels(P("Main Top", 0, legH, 0, w, top, d, PartKind.Top), FaceTop, new[] { FaceFront }, v["bevel"], v["bevelAngle"]), new Hole(1, w / 2, 80, v["cable"])),
                    EdgeBevels(P("Side Top", w - rd, legH, d, rd, top, rl - d, PartKind.Top), FaceTop, new[] { FaceLeft, FaceFront }, v["bevel"], v["bevelAngle"]),
                    P("Side Left", 0, 0, 0, t, legH, d, PartKind.Panel),
                    P("Side Right", w - t, 0, 0, t, legH, rl, PartKind.Panel),          // one panel under the whole right edge
                    P("End Panel", w - rd, 0, rl - t, rd - t, legH, t, PartKind.Panel), // holds the front end of the wing
                    P("Back Panel", t, legH - back, 50, w - 2 * t, back, t, PartKind.Panel),
                };
            });

        // ------------------------------------------------------------------ shelf
        private static PieceTemplate Shelf() => new("shelf", "Shelf",
            new[]
            {
                new ParamSpec("width", "Width", 800, 300, 1500, 10),
                new ParamSpec("depth", "Depth", 250, 120, 400, 5),
                new ParamSpec("thickness", "Board thickness", 20, 15, 40, 1),
                new ParamSpec("tiers", "Levels", 1, 1, 4, 1, Integer: true),
                new ParamSpec("spacing", "Level height", 300, 150, 500, 10),
                new ParamSpec("bracket", "Bracket thickness", 20, 15, 40, 1),
                new ParamSpec("inset", "Bracket inset", 60, 0, 200, 5),
                new ParamSpec("bevel", "Front edge bevel", 0, 0, 10, 1),
                new ParamSpec("bevelAngle", "Bevel angle", 45, 15, 75, 5),
            },
            v =>
            {
                double w = v["width"], d = v["depth"], st = v["thickness"], sp = v["spacing"], bt = v["bracket"];
                int tiers = (int)v["tiers"];
                double inset = Math.Min(v["inset"], w / 4);
                double bracketH = sp - st, bracketD = d * 0.8;
                double cleatH = Math.Min(60, bracketH - 5);
                const double cleatT = 15;
                var parts = new List<Part>();
                for (int i = 0; i < tiers; i++)
                {
                    double y = i * sp;                                            // each level stands on the one below
                    string tier = tiers > 1 ? $" {i + 1}" : "";
                    string? group = tiers > 1 ? $"Level {i + 1}" : null;
                    parts.Add(P($"Bracket{tier} Left", inset, y, 0, bt, bracketH, bracketD, PartKind.Board) with { Group = group });
                    parts.Add(P($"Bracket{tier} Right", w - inset - bt, y, 0, bt, bracketH, bracketD, PartKind.Board) with { Group = group });
                    parts.Add(P($"Wall Rail{tier}", inset + bt, y + bracketH - cleatH, 0, w - 2 * (inset + bt), cleatH, cleatT, PartKind.Board) with { Group = group });
                    parts.Add(EdgeBevels(P($"Shelf{tier}", 0, y + bracketH, 0, w, st, d, PartKind.Top), FaceTop, new[] { FaceFront }, v["bevel"], v["bevelAngle"]) with { Group = group });
                }
                return parts;
            });
    }
}
