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
        public static IReadOnlyList<PieceTemplate> All { get; } = new[] { Table(), Chair(), Bookshelf(), Cabinet(), FilingCabinet(), Door(), Desk(), DeskL(), Shelf() };

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
                const double gap = 2;
                const double knobFromEdge = 40;     // the knob hole sits this far from the edge opposite the hinge
                double knob = v["knob"];
                double doorW = doors == 0 ? 0 : (w - (doors - 1) * gap) / doors;
                double doorH = h - lift;
                for (int i = 0; i < doors; i++)
                {
                    // the first door is hinged on the left, so its knob is on the right; a second door is the mirror of it
                    double u = i == 1 ? knobFromEdge : doorW - knobFromEdge;
                    var door = P($"Door {i + 1}", i * (doorW + gap), lift, d - t, doorW, doorH, t, PartKind.Panel);
                    var hole = new Hole(2, u, doorH / 2, knob);   // punched through the door's thickness (Z)
                    door = FrontBevels(door, v["bevel"], v["bevelAngle"]) with { Group = $"Door {i + 1}" };
                    parts.Add(knob >= PieceHoles.MinDiameter ? door with { Cuts = PieceHoles.Sanitize(door.Size, new[] { hole }).Where(h => PieceBevels.HoleIsClear(door.Size, h, door.Bevels)).ToList() } : door);
                }
                return parts;
            });

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

                const double gap = 2, slide = 12;                               // between the fronts; room for the runners
                double ih = h - 2 * t - lift;
                double slot = ih / n;
                double frontH = (h - lift - (n - 1) * gap) / n;
                double side = Math.Min(12, t);
                double x0 = t + slide, x1 = w - t - slide, zb = BackThickness + 10;
                for (int i = 0; i < n; i++)
                {
                    int k = i + 1;
                    var front = FrontBevels(P($"Drawer {k} Front", 0, lift + i * (frontH + gap), dc, w, frontH, t, PartKind.Panel), v["bevel"], v["bevelAngle"]);
                    string group = $"Drawer {k}";   // front and box move, hide and pull out together
                    parts.Add(WithHole(front, new Hole(2, w / 2, frontH * 0.7, v["handle"])) with { Group = group });

                    // the box behind the front: it rides between the sides and touches the front
                    double y0 = lift + t + i * slot + 5, y1 = lift + t + (i + 1) * slot - 5;
                    parts.Add(P($"Drawer {k} Bottom", x0 + side, y0, zb, x1 - x0 - 2 * side, side, dc - zb, PartKind.Board) with { Group = group });
                    parts.Add(P($"Drawer {k} Side Left", x0, y0, zb, side, y1 - y0, dc - zb, PartKind.Board) with { Group = group });
                    parts.Add(P($"Drawer {k} Side Right", x1 - side, y0, zb, side, y1 - y0, dc - zb, PartKind.Board) with { Group = group });
                    parts.Add(P($"Drawer {k} Back", x0 + side, y0 + side, zb, x1 - x0 - 2 * side, y1 - y0 - side, side, PartKind.Board) with { Group = group });
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
