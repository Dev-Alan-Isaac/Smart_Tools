using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// A ready-made design you set with sliders and then insert as ordinary shapes. <see cref="Name"/> is the English fallback; the view
    /// looks it up as Str_Modeler_Template_&lt;id&gt; and each slider as Str_Modeler_Param_&lt;id&gt;_&lt;key&gt;.
    /// </summary>
    public sealed class SceneExample
    {
        private readonly Func<IReadOnlyDictionary<string, double>, List<SceneNode>> _make;

        public SceneExample(string id, string name, IReadOnlyList<ParamSpec> parameters, Func<IReadOnlyDictionary<string, double>, List<SceneNode>> make)
        {
            Id = id;
            Name = name;
            Params = parameters;
            _make = make;
        }

        public string Id { get; }

        public string Name { get; }

        public IReadOnlyList<ParamSpec> Params { get; }

        /// <summary>The sliders as given, each missing one at its default and each one inside its range.</summary>
        public IReadOnlyDictionary<string, double> Resolve(IReadOnlyDictionary<string, double>? values)
        {
            var resolved = new Dictionary<string, double>();
            foreach (var spec in Params)
            {
                double v = values != null && values.TryGetValue(spec.Key, out var given) && double.IsFinite(given) ? given : spec.Default;
                if (spec.Integer) v = Math.Round(v, MidpointRounding.AwayFromZero);
                resolved[spec.Key] = Math.Clamp(v, spec.Min, spec.Max);
            }
            return resolved;
        }

        /// <summary>The example as one node: a group named <paramref name="name"/> (or one shape when it is a single shape), standing on the floor.</summary>
        public SceneNode Build(IReadOnlyDictionary<string, double>? values, string? name = null)
        {
            var nodes = _make(Resolve(values));
            string title = string.IsNullOrWhiteSpace(name) ? Name : name;
            return nodes.Count == 1 ? nodes[0] : new GroupNode(Controllers.SceneEdit.NewId(), title, nodes);
        }
    }
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Every example: a few general ones made of shapes (a bracket with screw holes, a box with a lid, a phone stand), then the
    /// furniture pieces, which are still written as boards (<see cref="PieceTemplates"/>) and turned into shapes on the way.
    /// </summary>
    public static class SceneExamples
    {
        public static IReadOnlyList<SceneExample> All { get; } =
            new[] { Bracket(), BoxWithLid(), PhoneStand(), Hand() }
            .Concat(PieceTemplates.All.Select(t => new SceneExample(t.Id, t.Name, t.Params, v => LegacyPieces.FromParts(t.Build(v).Parts))))
            .ToList();

        public static SceneExample? Find(string? id) => All.FirstOrDefault(e => e.Id == id);

        private static string Id() => SceneEdit.NewId();

        private static SceneShape Box(string name, Vec3 centre, Vec3 size, bool hole = false) =>
            new(Id(), name, ShapeKind.Box, centre, size, Vec3.Zero, IsHole: hole);

        private static SceneShape Hole(string name, Vec3 centre, double diameter, double length, Vec3 rotation) =>
            new(Id(), name, ShapeKind.Cylinder, centre, new Vec3(diameter, length, diameter), rotation, IsHole: true);

        /// <summary>An L bracket: a base plate on the floor and an upright plate at its back, each with a row of screw holes.</summary>
        private static SceneExample Bracket() => new("bracket", "L bracket", new[]
        {
            new ParamSpec("width", "Width", 60, 20, 300),
            new ParamSpec("arm", "Arm length", 50, 20, 300),
            new ParamSpec("thickness", "Thickness", 4, 2, 20),
            new ParamSpec("hole", "Screw hole (0 = none)", 5, 0, 20, 0.5),
            new ParamSpec("holes", "Holes per arm", 2, 1, 4, 1, Integer: true),
        }, v =>
        {
            double w = v["width"], arm = v["arm"], t = v["thickness"], d = Math.Min(v["hole"], Math.Min(w / v["holes"], arm - t) * 0.8);
            int n = (int)v["holes"];
            var shapes = new List<SceneNode>
            {
                Box("Base plate", new Vec3(0, t / 2, arm / 2), new Vec3(w, t, arm)),
                Box("Upright plate", new Vec3(0, arm / 2, t / 2), new Vec3(w, arm, t)),
            };
            if (d >= 1)
                for (int i = 0; i < n; i++)
                {
                    double x = -w / 2 + w * (i + 0.5) / n, mid = t + (arm - t) / 2;
                    shapes.Add(Hole($"Base hole {i + 1}", new Vec3(x, t / 2, mid), d, t + 2, Vec3.Zero));
                    shapes.Add(Hole($"Upright hole {i + 1}", new Vec3(x, mid, t / 2), d, t + 2, new Vec3(90, 0, 0)));
                }
            return new List<SceneNode> { new GroupNode(Id(), "L bracket", shapes) };
        });

        /// <summary>An open box hollowed out by a box hole, and its lid laid beside it upside down (ready to print), with a lip that fits inside.</summary>
        private static SceneExample BoxWithLid() => new("box-lid", "Box with lid", new[]
        {
            new ParamSpec("width", "Width", 120, 30, 400),
            new ParamSpec("depth", "Depth", 80, 30, 400),
            new ParamSpec("height", "Height", 60, 15, 300),
            new ParamSpec("wall", "Wall", 3, 1, 15, 0.5),
            new ParamSpec("lid", "Lid (0 = none)", 1, 0, 1, 1, Integer: true),
        }, v =>
        {
            double w = v["width"], d = v["depth"], h = v["height"], t = Math.Min(v["wall"], Math.Min(w, d) / 4);
            var nodes = new List<SceneNode>
            {
                new GroupNode(Id(), "Box", new SceneNode[]
                {
                    Box("Box body", new Vec3(0, h / 2, 0), new Vec3(w, h, d)),
                    Box("Box inside", new Vec3(0, t + (h - t + 1) / 2, 0), new Vec3(w - 2 * t, h - t + 1, d - 2 * t), hole: true),
                }),
            };
            if (v["lid"] >= 1)
            {
                const double gap = 0.3, lip = 6;
                double lx = w + 20, lw = w - 2 * t - 2 * gap, ld = d - 2 * t - 2 * gap;
                nodes.Add(new GroupNode(Id(), "Lid", new SceneNode[]
                {
                    Box("Lid plate", new Vec3(lx, t / 2, 0), new Vec3(w, t, d)),
                    // the lip and its hole in a group of their own, so the hole hollows the lip and not the plate under it
                    new GroupNode(Id(), "Lid lip", new SceneNode[]
                    {
                        Box("Lid lip ring", new Vec3(lx, t + lip / 2, 0), new Vec3(lw, lip, ld)),
                        Box("Lid lip inside", new Vec3(lx, t + lip / 2, 0), new Vec3(lw - 2 * t, lip + 2, ld - 2 * t), hole: true),
                    }),
                }));
            }
            return nodes;
        });

        /// <summary>A phone stand: a base, a back leaning at the chosen angle, and a lip at the front that keeps the phone from sliding.</summary>
        private static SceneExample PhoneStand() => new("phone-stand", "Phone stand", new[]
        {
            new ParamSpec("width", "Width", 80, 40, 200),
            new ParamSpec("angle", "Angle from the table", 65, 45, 85),
            new ParamSpec("depth", "Base depth", 90, 50, 200),
            new ParamSpec("thickness", "Thickness", 6, 3, 15, 0.5),
            new ParamSpec("lip", "Lip height", 12, 4, 30),
        }, v =>
        {
            double w = v["width"], t = v["thickness"], depth = v["depth"], lip = v["lip"];
            double lean = 90 - v["angle"], back = 110;
            double r = lean * Math.PI / 180;
            double z0 = -depth / 2 + depth * 0.4;    // where the back meets the base
            var backCentre = new Vec3(0, t + back / 2 * Math.Cos(r), z0 - back / 2 * Math.Sin(r));
            return new List<SceneNode>
            {
                new GroupNode(Id(), "Phone stand", new SceneNode[]
                {
                    Box("Base", new Vec3(0, t / 2, 0), new Vec3(w, t, depth)),
                    new SceneShape(Id(), "Back", ShapeKind.Box, backCentre, new Vec3(w, back, t), new Vec3(-lean, 0, 0)),
                    Box("Lip", new Vec3(0, t + lip / 2, z0 + 14 + t), new Vec3(w, lip, t)),
                }),
            };
        });

        /// <summary>
        /// A hand lying palm down, fingers pointing away (-Z): a palm and wrist, a thumb of two segments and four fingers of three, each
        /// segment a cylinder a little thinner than the one before, with a round tip. Fingers fan out by the spread angle.
        /// </summary>
        private static SceneExample Hand() => new("hand", "Hand", new[]
        {
            new ParamSpec("width", "Palm width", 80, 30, 300),
            new ParamSpec("fingers", "Finger length (%)", 100, 60, 140),
            new ParamSpec("thickness", "Thickness", 24, 8, 80),
            new ParamSpec("spread", "Finger spread (degrees)", 6, 0, 20),
            new ParamSpec("thumb", "Thumb angle (degrees)", 40, 10, 80),
        }, v =>
        {
            double w = v["width"], t = Math.Min(v["thickness"], w * 0.6), spread = v["spread"];
            double palmDepth = w * 1.05;
            double fd = Math.Min(w / 4 * 0.92, t);            // finger thickness
            double baseLen = w * 0.95 * v["fingers"] / 100;   // the middle finger's length
            double y = t / 2;                                 // fingers lie at mid height of the palm
            const int Round = 12;                             // sides of the round parts: the join time grows fast with this

            var parts = new List<SceneNode>
            {
                Box("Palm", new Vec3(0, y, 0), new Vec3(w, t, palmDepth)),
                new SceneShape(Id(), "Wrist", ShapeKind.Cylinder, new Vec3(0, y, palmDepth / 2 + w * 0.25), new Vec3(w * 0.72, w * 0.5, t), new Vec3(90, 0, 0), Segments: Round),
            };

            // one jointed digit: from its base point, along the direction the yaw gives, in segments of the given shares of its length
            // side: -1 leans towards -X, +1 towards +X (the turn's sign is fixed here, whatever way the rotation maths counts it)
            void Digit(string name, Vec3 basePoint, double yaw, int side, double length, double thick, double[] shares)
            {
                Vec3 Dir(double turn)
                {
                    var d = Mat3.FromEuler(new Vec3(90, turn, 0)) * new Vec3(0, 1, 0);
                    return d.Z > 0 ? d * -1 : d;   // fingers point away from the wrist
                }
                var dir = Dir(yaw);
                if (Math.Abs(dir.X) > 1e-9 && Math.Sign(dir.X) != side)
                {
                    yaw = -yaw;
                    dir = Dir(yaw);
                }
                // one cylinder per digit, getting a little thinner segment by segment, and a round tip. (A ball at every joint looked
                // nicer but made the hand take ~17 s to join; this keeps it near a second.)
                double along = 0, thickness = thick;
                for (int s = 0; s < shares.Length; s++)
                {
                    double len = length * shares[s];
                    double overlap = s == 0 ? 0 : thickness * 0.15;   // segments overlap a little, so they join into one finger
                    var centre = basePoint + dir * (along - overlap + (len + overlap) / 2);
                    parts.Add(new SceneShape(Id(), $"{name} {s + 1}", ShapeKind.Cylinder, centre, new Vec3(thickness, len + overlap, thickness), new Vec3(90, yaw, 0), Segments: Round));
                    along += len;
                    if (s < shares.Length - 1) thickness *= 0.92;
                }
                parts.Add(new SceneShape(Id(), $"{name} tip", ShapeKind.Sphere, basePoint + dir * along, new Vec3(thickness, thickness, thickness), Vec3.Zero, Segments: Round));
            }

            var names = new[] { "Index finger", "Middle finger", "Ring finger", "Little finger" };
            var lengths = new[] { 0.9, 1.0, 0.94, 0.74 };
            var phalanges = new[] { 0.45, 0.31, 0.24 };
            for (int i = 0; i < 4; i++)
            {
                double x = -w / 2 + w * (i + 0.5) / 4;
                double thick = fd * (i == 3 ? 0.85 : 1);
                double yaw = (1.5 - i) * spread;              // the outer fingers lean out
                Digit(names[i], new Vec3(x, y, -palmDepth / 2 + thick * 0.25), Math.Abs(yaw), Math.Sign(yaw), baseLen * lengths[i], thick, phalanges);
            }
            Digit("Thumb", new Vec3(-w / 2 + fd * 0.3, y, palmDepth * 0.1), v["thumb"], -1, baseLen * 0.62, fd * 1.1, new[] { 0.55, 0.45 });

            return new List<SceneNode> { new GroupNode(Id(), "Hand", parts) };
        });
    }
}
