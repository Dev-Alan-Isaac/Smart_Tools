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
            new[] { Bracket(), BoxWithLid(), PhoneStand() }
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
    }
}
