using System.Text.RegularExpressions;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    public enum AlignMode
    {
        Min,
        Centre,
        Max,
    }
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Every change to a scene, as a function that returns a new scene (nothing is changed in place, so undo is a plain list of scenes).
    /// Nodes are named by id. Whatever comes in is made safe: sizes and positions kept in range, names kept unique, no NaN. Pure: no WPF.
    /// </summary>
    public static class SceneEdit
    {
        public const double MinSize = 0.1;
        public const double MaxSize = 10000;
        public const double MaxPosition = 100000;
        public const int MaxShapes = 500;

        private static readonly Regex TrailingNumber = new(@"\s+\d+$", RegexOptions.Compiled);

        public static string NewId() => Guid.NewGuid().ToString("N")[..12];

        // =================================================================
        //  Looking things up
        // =================================================================

        /// <summary>Every node, groups before their children.</summary>
        public static IEnumerable<SceneNode> All(IEnumerable<SceneNode> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                if (node is GroupNode g)
                    foreach (var child in All(g.Children)) yield return child;
            }
        }

        public static IEnumerable<SceneNode> All(Scene scene) => All(scene.Nodes);

        public static IEnumerable<SceneShape> Shapes(SceneNode node) => All(new[] { node }).OfType<SceneShape>();

        public static int ShapeCount(Scene scene) => All(scene).OfType<SceneShape>().Count();

        public static SceneNode? Find(Scene scene, string id) => All(scene).FirstOrDefault(n => n.Id == id);

        /// <summary>The top-level node a node is part of (itself when it is top-level), or null.</summary>
        public static SceneNode? TopOf(Scene scene, string id) =>
            scene.Nodes.FirstOrDefault(top => All(new[] { top }).Any(n => n.Id == id));

        /// <summary>The box around a node as it sits in the scene (holes included).</summary>
        public static Bounds3 BoundsOf(Scene scene, SceneNode node)
        {
            var b = Bounds3.Nothing;
            foreach (var s in Shapes(node)) b = b.With(Primitives.Bounds(s, scene.Meshes));
            return b;
        }

        public static Bounds3 BoundsOf(Scene scene, IEnumerable<string> ids)
        {
            var b = Bounds3.Nothing;
            foreach (var id in ids)
                if (Find(scene, id) is { } node) b = b.With(BoundsOf(scene, node));
            return b;
        }

        /// <summary>The ids made distinct and reduced to their top-level nodes (grouping and moving act on whole objects).</summary>
        public static List<string> TopIds(Scene scene, IEnumerable<string> ids) =>
            ids.Select(id => TopOf(scene, id)?.Id).OfType<string>().Distinct().ToList();

        // =================================================================
        //  Names
        // =================================================================

        /// <summary>The first free "Box", "Box 2", "Box 3"... (case does not matter; a number already at the end is replaced).</summary>
        public static string UniqueName(Scene scene, string? wanted, string? except = null)
        {
            string name = (wanted ?? "").Trim();
            if (name.Length == 0) name = "Shape";
            var taken = new HashSet<string>(All(scene).Where(n => n.Id != except).Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(name)) return name;
            string stem = TrailingNumber.Replace(name, "");
            for (int n = 2; ; n++)
            {
                string candidate = $"{stem} {n}";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        public static Scene Rename(Scene scene, string id, string? name)
        {
            string unique = UniqueName(scene, name, except: id);
            return Map(scene, n => n.Id == id ? WithName(n, unique) : n);
        }

        // =================================================================
        //  Adding and removing
        // =================================================================

        /// <summary>Adds a shape (a new id and a free name are given to it). Returns the same scene when the limit is reached.</summary>
        public static Scene Add(Scene scene, SceneShape shape, out string id)
        {
            id = "";
            if (ShapeCount(scene) >= MaxShapes) return scene;
            id = NewId();
            var added = Sanitize(shape) with { Id = id, Name = UniqueName(scene, shape.Name) };
            return scene with { Nodes = scene.Nodes.Append(added).ToList() };
        }

        /// <summary>Adds ready-made nodes (an example, a paste, an import): every id is new and every name made free.</summary>
        public static Scene AddNodes(Scene scene, IEnumerable<SceneNode> nodes, out List<string> ids, IReadOnlyDictionary<string, MeshData>? meshes = null)
        {
            ids = new List<string>();
            var result = scene;
            if (meshes != null)
            {
                var all = new Dictionary<string, MeshData>(scene.Meshes);
                foreach (var (key, mesh) in meshes) all[key] = mesh;
                result = result with { Meshes = all };
            }
            foreach (var node in nodes)
            {
                if (ShapeCount(result) + Shapes(node).Count() > MaxShapes) break;
                var copy = Fresh(result, node);
                result = result with { Nodes = result.Nodes.Append(copy).ToList() };
                ids.Add(copy.Id);
            }
            return result;
        }

        public static Scene Delete(Scene scene, IEnumerable<string> ids)
        {
            var gone = new HashSet<string>(ids);
            return Prune(scene with { Nodes = Remove(scene.Nodes, gone) });
        }

        /// <summary>Copies of the nodes, moved by <paramref name="offset"/>, placed right after each original.</summary>
        public static Scene Duplicate(Scene scene, IEnumerable<string> ids, Vec3 offset, out List<string> newIds)
        {
            var created = new List<string>();
            var wanted = new HashSet<string>(ids);
            var result = scene;
            foreach (var id in wanted)
            {
                if (Find(result, id) is not { } node) continue;
                if (ShapeCount(result) + Shapes(node).Count() > MaxShapes) break;
                var copy = Translate(Fresh(result, node), offset);
                result = result with { Nodes = InsertAfter(result.Nodes, id, copy) };
                created.Add(copy.Id);
            }
            newIds = created;
            return result;
        }

        // =================================================================
        //  Groups and holes
        // =================================================================

        /// <summary>Puts the top-level objects of <paramref name="ids"/> into one new group, where the first of them was. Needs two.</summary>
        public static Scene Group(Scene scene, IEnumerable<string> ids, string? name, out string groupId)
        {
            groupId = "";
            var tops = TopIds(scene, ids);
            if (tops.Count < 2) return scene;
            var members = scene.Nodes.Where(n => tops.Contains(n.Id)).ToList();
            int at = scene.Nodes.ToList().FindIndex(n => tops.Contains(n.Id));
            groupId = NewId();
            var group = new GroupNode(groupId, UniqueName(scene, string.IsNullOrWhiteSpace(name) ? "Group" : name), members);
            var nodes = scene.Nodes.Where(n => !tops.Contains(n.Id)).ToList();
            nodes.Insert(Math.Min(at, nodes.Count), group);
            return scene with { Nodes = nodes };
        }

        /// <summary>Takes the groups apart: their children take their place, in order.</summary>
        public static Scene Ungroup(Scene scene, IEnumerable<string> groupIds)
        {
            var ids = new HashSet<string>(groupIds);
            return scene with { Nodes = Splice(scene.Nodes, ids) };
        }

        /// <summary>
        /// Takes parts out of their groups: each becomes its own top-level object, placed right after the object it came from, where it
        /// was. A part that showed its group's colour keeps that colour. A group left with one member becomes that member, an empty
        /// one goes (as after a delete). Top-level nodes are left alone. <paramref name="moved"/> gets the ids that came out.
        /// </summary>
        public static Scene TakeOut(Scene scene, IEnumerable<string> ids, out List<string> moved)
        {
            moved = new List<string>();
            var result = scene;
            foreach (var id in ids.Distinct())
            {
                if (TopOf(result, id) is not { } top || top.Id == id || Find(result, id) is not { } node) continue;
                // the colour it showed: its own, else that of the nearest group above it that has one
                string? shown = node is SceneShape { Colour: { } own } ? own : node is GroupNode { Colour: { } gc } ? gc : null;
                if (shown == null)
                    foreach (var g in AncestorsOf(result, id))
                        if (g.Colour != null) { shown = g.Colour; break; }
                string? material = ShownMaterial(result, id);
                var outside = node switch
                {
                    SceneShape s => s with { Colour = shown, Material = material },
                    GroupNode g => g with { Colour = shown, Material = material },
                    _ => node,
                };
                var without = Remove(result.Nodes, new HashSet<string> { id });
                var list = new List<SceneNode>();
                foreach (var n in without)
                {
                    list.Add(n);
                    if (n.Id == top.Id) list.Add(outside);
                }
                result = Prune(result with { Nodes = list });
                moved.Add(id);
            }
            return result;
        }

        /// <summary>The groups a node sits in, nearest first.</summary>
        private static List<GroupNode> AncestorsOf(Scene scene, string id)
        {
            var path = new List<GroupNode>();
            bool Walk(IReadOnlyList<SceneNode> nodes)
            {
                foreach (var n in nodes)
                {
                    if (n.Id == id) return true;
                    if (n is GroupNode g)
                    {
                        path.Add(g);
                        if (Walk(g.Children)) return true;
                        path.RemoveAt(path.Count - 1);
                    }
                }
                return false;
            }
            Walk(scene.Nodes);
            path.Reverse();
            return path;
        }

        /// <summary>Makes every shape in the nodes a hole (true) or a solid (false).</summary>
        public static Scene SetHole(Scene scene, IEnumerable<string> ids, bool hole) =>
            MapShapes(scene, ids, s => s with { IsHole = hole });

        /// <summary>
        /// Paints the nodes: a shape gets the colour; a group gets it as its own and its parts lose theirs, so all of it shows the
        /// colour (a part painted afterwards keeps its own colour inside the group). Null = back to the default.
        /// </summary>
        public static Scene SetColour(Scene scene, IEnumerable<string> ids, string? hex)
        {
            string? colour = PiecePaint.Normalize(hex);
            var set = new HashSet<string>(ids);
            return Map(scene, n => !set.Contains(n.Id) ? n : n switch
            {
                SceneShape s => s with { Colour = colour },
                GroupNode g => ClearColours(g) with { Colour = colour },
                _ => n,
            });
        }

        /// <summary>
        /// Sets what the nodes are made of (<see cref="SceneMaterial"/> id, null = standard), like <see cref="SetColour"/>: a group gets it
        /// as its own and its parts lose theirs, so all of it shows the material.
        /// </summary>
        public static Scene SetMaterial(Scene scene, IEnumerable<string> ids, string? material)
        {
            string? value = SceneMaterial.Normalize(material);
            var set = new HashSet<string>(ids);
            return Map(scene, n => !set.Contains(n.Id) ? n : n switch
            {
                SceneShape s => s with { Material = value },
                GroupNode g => ClearMaterials(g) with { Material = value },
                _ => n,
            });
        }

        /// <summary>The material a node shows: its own, else that of the nearest group above it that has one (null = standard).</summary>
        public static string? ShownMaterial(Scene scene, string id)
        {
            var own = Find(scene, id) switch { SceneShape s => s.Material, GroupNode g => g.Material, _ => null };
            return own ?? AncestorsOf(scene, id).Select(g => g.Material).FirstOrDefault(m => m != null);
        }

        public static Scene SetHidden(Scene scene, IEnumerable<string> ids, bool hidden)
        {
            var set = new HashSet<string>(ids);
            return Map(scene, n => !set.Contains(n.Id) ? n : n switch
            {
                SceneShape s => s with { Hidden = hidden },
                GroupNode g => g with { Hidden = hidden },
                _ => n,
            });
        }

        public static Scene ShowAll(Scene scene) => Map(scene, n => n switch
        {
            SceneShape { Hidden: true } s => s with { Hidden = false },
            GroupNode { Hidden: true } g => g with { Hidden = false },
            _ => n,
        });

        public static Scene SetLocked(Scene scene, IEnumerable<string> ids, bool locked)
        {
            var set = new HashSet<string>(ids);
            return Map(scene, n => !set.Contains(n.Id) ? n : n switch
            {
                SceneShape s => s with { Locked = locked },
                GroupNode g => g with { Locked = locked },
                _ => n,
            });
        }

        /// <summary>Replaces one shape (by its id) with a changed copy, made safe.</summary>
        public static Scene Replace(Scene scene, SceneShape shape)
        {
            var safe = Sanitize(shape);
            return Map(scene, n => n.Id == shape.Id ? safe : n);
        }

        // =================================================================
        //  Moving, turning, mirroring, lining up
        // =================================================================

        public static Scene Translate(Scene scene, IEnumerable<string> ids, Vec3 delta) =>
            MapShapes(scene, ids, s => s with { Position = Clamp(s.Position + delta) });

        /// <summary>Moves each node so it stands on the floor (its lowest point at Y = 0).</summary>
        public static Scene DropToFloor(Scene scene, IEnumerable<string> ids)
        {
            var result = scene;
            foreach (var id in ids.Distinct())
            {
                if (Find(result, id) is not { } node) continue;
                var b = BoundsOf(result, node);
                if (!b.IsEmpty) result = Translate(result, new[] { id }, new Vec3(0, -b.Min.Y, 0));
            }
            return result;
        }

        /// <summary>Lines the nodes up on one axis (0 X, 1 Y, 2 Z) with the box around all of them: its low side, middle or high side.</summary>
        public static Scene Align(Scene scene, IEnumerable<string> ids, int axis, AlignMode mode)
        {
            var list = ids.Distinct().ToList();
            var all = BoundsOf(scene, list);
            if (all.IsEmpty || list.Count < 2) return scene;
            double target = Pick(all, axis, mode);
            var result = scene;
            foreach (var id in list)
            {
                if (Find(result, id) is not { } node) continue;
                double now = Pick(BoundsOf(result, node), axis, mode);
                result = Translate(result, new[] { id }, Unit(axis) * (target - now));
            }
            return result;
        }

        /// <summary>Turns the nodes together about the middle of the box around them, by <paramref name="degrees"/> round a scene axis.</summary>
        public static Scene Rotate(Scene scene, IEnumerable<string> ids, int axis, double degrees)
        {
            var list = ids.Distinct().ToList();
            var centre = BoundsOf(scene, list).Centre;
            var turn = axis == 0 ? Mat3.RotationX(degrees) : axis == 1 ? Mat3.RotationY(degrees) : Mat3.RotationZ(degrees);
            return MapShapes(scene, list, s => s with
            {
                Position = Clamp(centre + turn * (s.Position - centre)),
                Rotation = (turn * Mat3.FromEuler(s.Rotation)).ToEuler(),
            });
        }

        /// <summary>Mirrors the nodes across the middle of the box around them, on a scene axis (0 X, 1 Y, 2 Z).</summary>
        public static Scene Mirror(Scene scene, IEnumerable<string> ids, int axis)
        {
            var list = ids.Distinct().ToList();
            var centre = BoundsOf(scene, list).Centre;
            var w = Mat3.Scale(new Vec3(axis == 0 ? -1 : 1, axis == 1 ? -1 : 1, axis == 2 ? -1 : 1));
            // W * R * F = R' * F' with F' the other mirror state: R' = W * R * FlipX (a true turn again)
            return MapShapes(scene, list, s => s with
            {
                Position = Clamp(centre + w * (s.Position - centre)),
                Rotation = (w * Mat3.FromEuler(s.Rotation) * Mat3.FlipX).ToEuler(),
                Mirrored = !s.Mirrored,
            });
        }

        /// <summary>
        /// Stretches a node so the box around it becomes <paramref name="to"/>. Shapes turned square to the axes scale exactly; a shape
        /// turned at another angle takes the stretch along its own sides as well as it can.
        /// </summary>
        public static Scene Resize(Scene scene, string id, Bounds3 to)
        {
            if (Find(scene, id) is not { } node) return scene;
            var from = BoundsOf(scene, node);
            if (from.IsEmpty) return scene;
            var fs = from.Size;
            var ts = to.Size;
            var f = new Vec3(Ratio(ts.X, fs.X), Ratio(ts.Y, fs.Y), Ratio(ts.Z, fs.Z));
            return MapShapes(scene, new[] { id }, s =>
            {
                var r = Mat3.FromEuler(s.Rotation);
                double Along(Vec3 local)
                {
                    var d = r * local;   // where the shape's own side points in the scene
                    return Math.Sqrt(d.X * d.X * f.X * f.X + d.Y * d.Y * f.Y * f.Y + d.Z * d.Z * f.Z * f.Z);
                }
                var rel = s.Position - from.Min;
                return Sanitize(s with
                {
                    Position = to.Min + new Vec3(rel.X * f.X, rel.Y * f.Y, rel.Z * f.Z),
                    Size = new Vec3(s.Size.X * Along(new Vec3(1, 0, 0)), s.Size.Y * Along(new Vec3(0, 1, 0)), s.Size.Z * Along(new Vec3(0, 0, 1))),
                });
            });
        }

        // =================================================================
        //  Making things safe
        // =================================================================

        public static SceneShape Sanitize(SceneShape s)
        {
            static double Num(double v, double fallback) => double.IsFinite(v) ? v : fallback;
            static double Size(double v) => Math.Clamp(Num(v, 10), MinSize, MaxSize);
            static double Angle(double v)
            {
                double a = Num(v, 0) % 360;
                if (a > 180) a -= 360;
                if (a <= -180) a += 360;
                return Math.Round(a, 6) == 0 ? 0 : Math.Round(a, 6);
            }
            return s with
            {
                Name = string.IsNullOrWhiteSpace(s.Name) ? "Shape" : s.Name.Trim(),
                Kind = Enum.IsDefined(s.Kind) ? s.Kind : ShapeKind.Box,
                Position = Clamp(new Vec3(Num(s.Position.X, 0), Num(s.Position.Y, 0), Num(s.Position.Z, 0))),
                Size = new Vec3(Size(s.Size.X), Size(s.Size.Y), Size(s.Size.Z)),
                Rotation = new Vec3(Angle(s.Rotation.X), Angle(s.Rotation.Y), Angle(s.Rotation.Z)),
                Colour = PiecePaint.Normalize(s.Colour),
                Material = SceneMaterial.Normalize(s.Material),
                Segments = Math.Clamp(s.Segments, Primitives.MinSegments, Primitives.MaxSegments),
                Inner = Math.Clamp(Num(s.Inner, 0.6), 0.05, 0.95),
            };
        }

        // =================================================================
        //  Tree plumbing
        // =================================================================

        /// <summary>Rewrites every node (children before their group), keeping what <paramref name="change"/> returns unchanged.</summary>
        public static Scene Map(Scene scene, Func<SceneNode, SceneNode> change) => scene with { Nodes = MapList(scene.Nodes, change) };

        private static IReadOnlyList<SceneNode> MapList(IReadOnlyList<SceneNode> nodes, Func<SceneNode, SceneNode> change) =>
            nodes.Select(n => n is GroupNode g ? change(g with { Children = MapList(g.Children, change) }) : change(n)).ToList();

        /// <summary>Changes every shape inside the given nodes (each shape once, even when a group and its member are both given).</summary>
        private static Scene MapShapes(Scene scene, IEnumerable<string> ids, Func<SceneShape, SceneShape> change)
        {
            var targets = new HashSet<string>();
            foreach (var id in ids)
                if (Find(scene, id) is { } node)
                    foreach (var s in Shapes(node)) targets.Add(s.Id);
            return Map(scene, n => n is SceneShape s && targets.Contains(s.Id) ? change(s) : n);
        }

        private static SceneNode Translate(SceneNode node, Vec3 delta) => node switch
        {
            SceneShape s => s with { Position = Clamp(s.Position + delta) },
            GroupNode g => g with { Children = g.Children.Select(c => Translate(c, delta)).ToList() },
            _ => node,
        };

        /// <summary>A copy of a node with new ids all through and free names (checked against the scene and against each other).</summary>
        private static SceneNode Fresh(Scene scene, SceneNode node)
        {
            var taken = new HashSet<string>(All(scene).Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
            string Free(string name)
            {
                string n = string.IsNullOrWhiteSpace(name) ? "Shape" : name.Trim();
                if (taken.Add(n)) return n;
                string stem = TrailingNumber.Replace(n, "");
                for (int i = 2; ; i++)
                    if (taken.Add($"{stem} {i}")) return $"{stem} {i}";
            }
            SceneNode Copy(SceneNode n) => n switch
            {
                SceneShape s => Sanitize(s) with { Id = NewId(), Name = Free(s.Name) },
                GroupNode g => g with { Id = NewId(), Name = Free(g.Name), Children = g.Children.Select(Copy).ToList() },
                _ => n,
            };
            return Copy(node);
        }

        private static IReadOnlyList<SceneNode> Remove(IReadOnlyList<SceneNode> nodes, HashSet<string> gone) =>
            nodes.Where(n => !gone.Contains(n.Id))
                 .Select(n => n is GroupNode g ? g with { Children = Remove(g.Children, gone) } : n)
                 .ToList();

        /// <summary>Drops groups left empty and turns groups of one into that one member.</summary>
        private static Scene Prune(Scene scene)
        {
            IReadOnlyList<SceneNode> Clean(IReadOnlyList<SceneNode> nodes) => nodes.SelectMany(n =>
            {
                if (n is not GroupNode g) return new[] { n };
                var kids = Clean(g.Children);
                if (kids.Count == 0) return Array.Empty<SceneNode>();
                if (kids.Count == 1) return new[] { kids[0] };
                return new SceneNode[] { g with { Children = kids } };
            }).ToList();
            return scene with { Nodes = Clean(scene.Nodes) };
        }

        private static IReadOnlyList<SceneNode> Splice(IReadOnlyList<SceneNode> nodes, HashSet<string> groups) =>
            nodes.SelectMany(n => n is GroupNode g
                ? groups.Contains(g.Id) ? Splice(g.Children, groups) : new SceneNode[] { g with { Children = Splice(g.Children, groups) } }
                : new[] { n }).ToList();

        private static IReadOnlyList<SceneNode> InsertAfter(IReadOnlyList<SceneNode> nodes, string id, SceneNode added)
        {
            var list = new List<SceneNode>();
            foreach (var n in nodes)
            {
                var kept = n is GroupNode g ? g with { Children = InsertAfter(g.Children, id, added) } : n;
                list.Add(kept);
                if (n.Id == id) list.Add(added);
            }
            return list;
        }

        private static GroupNode ClearMaterials(GroupNode g) => g with
        {
            Material = null,
            Children = g.Children.Select(c => c switch
            {
                SceneShape s => s with { Material = null },
                GroupNode inner => ClearMaterials(inner),
                _ => c,
            }).ToList(),
        };

        private static GroupNode ClearColours(GroupNode g) => g with
        {
            Colour = null,
            Children = g.Children.Select(c => c switch
            {
                SceneShape s => s with { Colour = null },
                GroupNode inner => ClearColours(inner),
                _ => c,
            }).ToList(),
        };

        private static SceneNode WithName(SceneNode node, string name) => node switch
        {
            SceneShape s => s with { Name = name },
            GroupNode g => g with { Name = name },
            _ => node,
        };

        private static Vec3 Clamp(Vec3 v) => new(
            Math.Clamp(v.X, -MaxPosition, MaxPosition), Math.Clamp(v.Y, -MaxPosition, MaxPosition), Math.Clamp(v.Z, -MaxPosition, MaxPosition));

        private static double Pick(Bounds3 b, int axis, AlignMode mode)
        {
            double lo = axis == 0 ? b.Min.X : axis == 1 ? b.Min.Y : b.Min.Z;
            double hi = axis == 0 ? b.Max.X : axis == 1 ? b.Max.Y : b.Max.Z;
            return mode == AlignMode.Min ? lo : mode == AlignMode.Max ? hi : (lo + hi) / 2;
        }

        private static Vec3 Unit(int axis) => axis == 0 ? new Vec3(1, 0, 0) : axis == 1 ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);

        private static double Ratio(double to, double from) => from < 1e-9 ? 1 : Math.Max(to, MinSize) / from;
    }

    /// <summary>Undo and redo of whole scenes. Every change pushes the scene it started from; nothing is edited in place.</summary>
    public sealed class SceneHistory
    {
        public const int Limit = 100;

        private readonly List<Scene> _undo = new();
        private readonly List<Scene> _redo = new();

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }

        /// <summary>Call with the scene as it was BEFORE a change. A change that leaves it the same is not recorded.</summary>
        public void Push(Scene before, Scene after)
        {
            if (before.Equals(after)) return;
            _undo.Add(before);
            if (_undo.Count > Limit) _undo.RemoveAt(0);
            _redo.Clear();
        }

        public Scene? Undo(Scene current)
        {
            if (_undo.Count == 0) return null;
            var back = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(current);
            return back;
        }

        public Scene? Redo(Scene current)
        {
            if (_redo.Count == 0) return null;
            var forward = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(current);
            return forward;
        }
    }
}
