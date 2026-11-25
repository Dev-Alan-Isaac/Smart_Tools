using System.Text.Json;
using System.Text.RegularExpressions;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>One board of a custom piece as it is written to a file or to the settings. Plain values, no WPF.</summary>
    public sealed class CustomPartDto
    {
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public double D { get; set; }
        public string Kind { get; set; } = nameof(PartKind.Board);
        public List<CustomHoleDto>? Holes { get; set; }
        public List<CustomBevelDto>? Bevels { get; set; }
        public string? Group { get; set; }
    }

    /// <summary>An angled edge cut as it is written to a file (see <see cref="Bevel"/>).</summary>
    public sealed class CustomBevelDto
    {
        public int FaceA { get; set; }
        public int FaceB { get; set; }
        public double Angle { get; set; }
        public double Depth { get; set; }
    }

    /// <summary>A round hole of a board as it is written to a file (see <see cref="Hole"/>).</summary>
    public sealed class CustomHoleDto
    {
        public int Axis { get; set; }
        public double U { get; set; }
        public double V { get; set; }
        public double Diameter { get; set; }
    }

    /// <summary>A saved design (.dmodel): a name and its boards. <see cref="Version"/> is for changes to this format later.</summary>
    public sealed class CustomPieceDto
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "";
        public List<CustomPartDto> Parts { get; set; } = new();
    }

    /// <summary>Two boards that share space.</summary>
    public sealed record OverlapPair(string First, string Second);
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The rules for editing a custom piece: boards are added, copied, removed and changed as whole new lists (nothing is edited in
    /// place), and everything typed is made safe - sizes and positions kept in range, names kept unique, no NaN.
    /// </summary>
    public static class PieceEditor
    {
        public const double MinSize = 1;
        public const double MaxSize = 10000;
        public const double MaxPosition = 10000;
        public const int MaxParts = 200;

        private static readonly Regex TrailingNumber = new(@"\s+\d+$", RegexOptions.Compiled);

        /// <summary>The first free "Board 1", "Board 2"... (case does not matter).</summary>
        public static string UniqueName(IEnumerable<string> existing, string baseName)
        {
            var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            for (int n = 1; ; n++)
            {
                string candidate = $"{baseName} {n}";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        /// <summary>A name somebody typed: kept as typed when free, else " 2", " 3"... added. Empty becomes a numbered board.</summary>
        public static string UniqueTypedName(IEnumerable<string> existing, string? typed)
        {
            string name = (typed ?? "").Trim();
            if (name.Length == 0) return UniqueName(existing, "Board");
            var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(name)) return name;
            for (int n = 2; ; n++)
            {
                string candidate = $"{name} {n}";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        private static Vec3 DefaultSize(PartKind kind) => kind switch
        {
            PartKind.Leg => new Vec3(40, 700, 40),
            PartKind.Panel => new Vec3(18, 600, 400),
            PartKind.Top => new Vec3(1000, 30, 600),
            _ => new Vec3(600, 18, 300),
        };

        /// <summary>A new board of a kind, placed beside the piece (touching its right side, on the floor) so it starts inside nothing.</summary>
        public static IReadOnlyList<Part> Add(IReadOnlyList<Part> parts, PartKind kind)
        {
            var result = parts.ToList();
            if (result.Count >= MaxParts) return result;
            double x = result.Count == 0 ? 0 : result.Max(p => p.Max.X);
            result.Add(new Part(UniqueName(result.Select(p => p.Name), kind.ToString()), new Vec3(x, 0, 0), DefaultSize(kind), kind));
            return result;
        }

        /// <summary>A copy of one board right beside it, with its own name, placed after it in the list.</summary>
        public static IReadOnlyList<Part> Duplicate(IReadOnlyList<Part> parts, int index)
        {
            var result = parts.ToList();
            if (index < 0 || index >= result.Count || result.Count >= MaxParts) return result;
            var source = result[index];
            string baseName = TrailingNumber.Replace(source.Name, "");
            var copy = source with { Name = UniqueName(result.Select(p => p.Name), baseName), Min = new Vec3(source.Max.X, source.Min.Y, source.Min.Z) };
            result.Insert(index + 1, Sanitize(copy, Array.Empty<string>()) with { Name = copy.Name });
            return result;
        }

        public static IReadOnlyList<Part> Remove(IReadOnlyList<Part> parts, int index)
        {
            var result = parts.ToList();
            if (index >= 0 && index < result.Count) result.RemoveAt(index);
            return result;
        }

        /// <summary>One board swapped for a changed one, made safe against the others.</summary>
        public static IReadOnlyList<Part> Replace(IReadOnlyList<Part> parts, int index, Part changed)
        {
            var result = parts.ToList();
            if (index < 0 || index >= result.Count) return result;
            var others = result.Where((_, i) => i != index).Select(p => p.Name).ToList();
            result[index] = Sanitize(changed, others);
            return result;
        }

        /// <summary>The board with its size and position kept in range, no NaN, and a name not used by <paramref name="otherNames"/>.</summary>
        public static Part Sanitize(Part part, IEnumerable<string> otherNames)
        {
            static double Pos(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, -MaxPosition, MaxPosition);
            static double Size(double v) => double.IsNaN(v) ? MinSize : Math.Clamp(v, MinSize, MaxSize);
            var size = new Vec3(Size(part.Size.X), Size(part.Size.Y), Size(part.Size.Z));
            var bevels = PieceBevels.Sanitize(size, part.Bevels);
            var holes = PieceHoles.Sanitize(size, part.Holes).Where(h => PieceBevels.HoleIsClear(size, h, bevels)).ToList();   // a hole never runs into a cut
            string? group = string.IsNullOrWhiteSpace(part.Group) ? null : part.Group.Trim()[..Math.Min(60, part.Group.Trim().Length)];
            return new Part(
                UniqueTypedName(otherNames, part.Name),
                new Vec3(Pos(part.Min.X), Pos(part.Min.Y), Pos(part.Min.Z)),
                size,
                part.Kind,
                holes.Count == 0 ? null : holes,   // a board made smaller pulls its holes back inside
                bevels.Count == 0 ? null : bevels,
                group);
        }

        public static PieceModel ToModel(string name, IReadOnlyList<Part> parts) => new(name, parts.ToList());

        /// <summary>The boards of a finished piece (a preset) as a list to edit.</summary>
        public static IReadOnlyList<Part> FromModel(PieceModel model) => model.Parts.ToList();
    }

    /// <summary>Is the piece sound? Boards sharing space, and boards not touching the rest, are the two mistakes a hand-made piece can have.</summary>
    public static class PieceChecks
    {
        private const double Tolerance = 0.01;   // mm: a hair of overlap from rounding is not a mistake

        private static double Penetration(double a0, double a1, double b0, double b1) => Math.Min(a1, b1) - Math.Max(a0, b0);

        // two boxes that cross may still not share material when a bevel cut it away (mitred corners): PieceBevels checks that
        private static bool Overlap(Part a, Part b) => PieceBevels.SolidsOverlap(a, b, Tolerance);

        private static bool Touch(Part a, Part b) =>
            Penetration(a.Min.X, a.Max.X, b.Min.X, b.Max.X) >= -Tolerance &&
            Penetration(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y) >= -Tolerance &&
            Penetration(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z) >= -Tolerance;

        public static IReadOnlyList<OverlapPair> Overlapping(IReadOnlyList<Part> parts)
        {
            var pairs = new List<OverlapPair>();
            for (int i = 0; i < parts.Count; i++)
                for (int j = i + 1; j < parts.Count; j++)
                    if (Overlap(parts[i], parts[j])) pairs.Add(new OverlapPair(parts[i].Name, parts[j].Name));
            return pairs;
        }

        /// <summary>The boards that are not part of the biggest group of boards touching each other (what would fall off).</summary>
        public static IReadOnlyList<string> Loose(IReadOnlyList<Part> parts)
        {
            if (parts.Count < 2) return Array.Empty<string>();
            var group = new int[parts.Count];
            Array.Fill(group, -1);
            int groups = 0;
            for (int start = 0; start < parts.Count; start++)
            {
                if (group[start] >= 0) continue;
                var queue = new Queue<int>();
                queue.Enqueue(start);
                group[start] = groups;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    for (int j = 0; j < parts.Count; j++)
                        if (group[j] < 0 && Touch(parts[i], parts[j])) { group[j] = groups; queue.Enqueue(j); }
                }
                groups++;
            }
            int biggest = Enumerable.Range(0, groups).OrderByDescending(g => group.Count(x => x == g)).ThenBy(g => g).First();
            return parts.Where((_, i) => group[i] != biggest).Select(p => p.Name).ToList();
        }
    }

    /// <summary>The design file (.dmodel, readable JSON) and the same boards stored in the settings. Whatever is read is made safe first.</summary>
    public static class CustomPieceJson
    {
        public const string DefaultName = "My piece";

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static List<CustomPartDto> ToDtos(IReadOnlyList<Part> parts) => parts.Select(p => new CustomPartDto
        {
            Name = p.Name, X = p.Min.X, Y = p.Min.Y, Z = p.Min.Z, W = p.Size.X, H = p.Size.Y, D = p.Size.Z, Kind = p.Kind.ToString(),
            Holes = p.Holes.Count == 0 ? null : p.Holes.Select(h => new CustomHoleDto { Axis = h.Axis, U = h.U, V = h.V, Diameter = h.Diameter }).ToList(),
            Bevels = p.Bevels.Count == 0 ? null : p.Bevels.Select(b => new CustomBevelDto { FaceA = b.FaceA, FaceB = b.FaceB, Angle = b.Angle, Depth = b.Depth }).ToList(),
            Group = p.Group,
        }).ToList();

        public static IReadOnlyList<Part> FromDtos(IEnumerable<CustomPartDto>? dtos)
        {
            var parts = new List<Part>();
            if (dtos == null) return parts;
            foreach (var d in dtos.Where(d => d != null).Take(PieceEditor.MaxParts))
            {
                var kind = Enum.TryParse<PartKind>(d.Kind, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : PartKind.Board;
                var holes = d.Holes?.Where(h => h != null).Select(h => new Hole(h.Axis, h.U, h.V, h.Diameter)).ToList();
                var bevels = d.Bevels?.Where(b => b != null).Select(b => new Bevel(b.FaceA, b.FaceB, b.Angle, b.Depth)).ToList();
                parts.Add(PieceEditor.Sanitize(new Part(d.Name, new Vec3(d.X, d.Y, d.Z), new Vec3(d.W, d.H, d.D), kind, holes, bevels, d.Group), parts.Select(p => p.Name)));
            }
            return parts;
        }

        public static string ToJson(string name, IReadOnlyList<Part> parts) =>
            JsonSerializer.Serialize(new CustomPieceDto { Name = name, Parts = ToDtos(parts) }, Options);

        public static bool TryRead(string? json, out string name, out IReadOnlyList<Part> parts, out string error)
        {
            name = DefaultName;
            parts = Array.Empty<Part>();
            error = "";
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "The file is empty.";
                return false;
            }
            try
            {
                var dto = JsonSerializer.Deserialize<CustomPieceDto>(json, Options);
                if (dto == null)
                {
                    error = "The file has no design in it.";
                    return false;
                }
                name = string.IsNullOrWhiteSpace(dto.Name) ? DefaultName : dto.Name.Trim();
                parts = FromDtos(dto.Parts);
                return true;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
