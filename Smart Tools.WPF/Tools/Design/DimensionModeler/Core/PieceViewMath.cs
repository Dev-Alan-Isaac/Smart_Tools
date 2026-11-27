using System.Globalization;
using System.Text.RegularExpressions;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Where the measuring lines go, as maths only: the view draws them.</summary>
    public static class DimensionLines
    {
        /// <summary>Width, height and depth of the whole piece, drawn <paramref name="offset"/> mm outside it. Empty when there is nothing.</summary>
        public static IReadOnlyList<DimensionLine> Overall(PieceModel model, double offset) =>
            model.Parts.Count == 0 ? Array.Empty<DimensionLine>() : Around(model.Min, model.Max, offset);

        /// <summary>The three measures of one board.</summary>
        public static IReadOnlyList<DimensionLine> ForPart(Part part, double offset) => Around(part.Min, part.Max, offset);

        /// <summary>
        /// The free space around a board: for each of its six faces, the distance to the nearest other board that lies straight
        /// across from it (their faces overlap). Boards for which <paramref name="ignore"/> is true - hidden ones, such as a cabinet
        /// door - do not count, so the gaps are measured to what is inside. Faces that touch, or look at nothing, give no line.
        /// </summary>
        public static IReadOnlyList<DimensionLine> Gaps(IReadOnlyList<Part> parts, Part part, Func<Part, bool>? ignore = null)
        {
            const double Touch = 0.5;
            var lines = new List<DimensionLine>();
            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                for (int side = 0; side < 2; side++)
                {
                    bool high = side == 1;
                    double face = high ? At(part.Max, axis) : At(part.Min, axis);
                    Part? best = null;
                    double bestGap = double.PositiveInfinity;
                    foreach (var other in parts)
                    {
                        if (ReferenceEquals(other, part) || other.Name == part.Name || (ignore?.Invoke(other) ?? false)) continue;
                        if (Math.Min(At(part.Max, u), At(other.Max, u)) - Math.Max(At(part.Min, u), At(other.Min, u)) <= Touch) continue;
                        if (Math.Min(At(part.Max, v), At(other.Max, v)) - Math.Max(At(part.Min, v), At(other.Min, v)) <= Touch) continue;
                        double gap = high ? At(other.Min, axis) - face : face - At(other.Max, axis);
                        if (gap > Touch && gap < bestGap)
                        {
                            best = other;
                            bestGap = gap;
                        }
                    }
                    if (best == null) continue;
                    double cu = (Math.Max(At(part.Min, u), At(best.Min, u)) + Math.Min(At(part.Max, u), At(best.Max, u))) / 2;
                    double cv = (Math.Max(At(part.Min, v), At(best.Min, v)) + Math.Min(At(part.Max, v), At(best.Max, v))) / 2;
                    double near = high ? face : face - bestGap, far = high ? face + bestGap : face;
                    lines.Add(new DimensionLine(Point(axis, near, u, cu, v, cv), Point(axis, far, u, cu, v, cv), Vec3.Zero, bestGap));
                }
            }
            return lines;
        }

        private static double At(Vec3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

        private static Vec3 Point(int a, double av, int b, double bv, int c, double cv)
        {
            var values = new double[3];
            values[a] = av;
            values[b] = bv;
            values[c] = cv;
            return new Vec3(values[0], values[1], values[2]);
        }

        private static IReadOnlyList<DimensionLine> Around(Vec3 min, Vec3 max, double off) => new[]
        {
            // width along the front bottom edge, pushed out in front of the piece
            new DimensionLine(new Vec3(min.X, min.Y, max.Z), new Vec3(max.X, min.Y, max.Z), new Vec3(0, 0, off), max.X - min.X),
            // height up the front left edge, pushed out to the left
            new DimensionLine(new Vec3(min.X, min.Y, max.Z), new Vec3(min.X, max.Y, max.Z), new Vec3(-off, 0, 0), max.Y - min.Y),
            // depth along the right bottom edge, pushed out to the right
            new DimensionLine(new Vec3(max.X, min.Y, min.Z), new Vec3(max.X, min.Y, max.Z), new Vec3(off, 0, 0), max.Z - min.Z),
        };
    }

    public static class LengthFormat
    {
        /// <summary>"1200 mm", "75.6 cm", "47.24 in" - always a dot for decimals, no trailing zeros.</summary>
        public static string Format(double mm, LengthUnit unit)
        {
            var c = CultureInfo.InvariantCulture;
            return unit switch
            {
                LengthUnit.Centimetres => (mm / 10).ToString("0.#", c) + " cm",
                LengthUnit.Inches => (mm / 25.4).ToString("0.##", c) + " in",
                _ => mm.ToString("0.#", c) + " mm",
            };
        }

        public static double MmPerUnit(LengthUnit unit) => unit switch
        {
            LengthUnit.Centimetres => 10,
            LengthUnit.Inches => 25.4,
            _ => 1,
        };

        public static string Suffix(LengthUnit unit) => unit switch
        {
            LengthUnit.Centimetres => "cm",
            LengthUnit.Inches => "in",
            _ => "mm",
        };

        /// <summary>The number alone, in the unit, for a box: "1200", "120", "47.244".</summary>
        public static string Number(double mm, LengthUnit unit) =>
            (mm / MmPerUnit(unit)).ToString(unit == LengthUnit.Inches ? "0.###" : "0.##", CultureInfo.InvariantCulture);

        private static readonly Regex Typed = new(@"^(?<n>[-+]?[0-9.,]+)\s*(?<u>mm|cm|in|""|m)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Text typed in a box, in millimetres. A unit written after the number ("2 in", "30cm", "1,2 m") wins over the box's unit.</summary>
        public static bool TryParseMm(string? text, LengthUnit boxUnit, out double mm)
        {
            mm = 0;
            var m = Typed.Match((text ?? "").Trim());
            if (!m.Success || !NumberInput.TryParse(m.Groups["n"].Value, out double value)) return false;
            double per = m.Groups["u"].Value.ToLowerInvariant() switch
            {
                "mm" => 1,
                "cm" => 10,
                "in" or "\"" => 25.4,
                "m" => 1000,
                _ => MmPerUnit(boxUnit),
            };
            mm = value * per;
            return true;
        }
    }

    /// <summary>One axis of the corner gizmo: 0 = X, 1 = Y, 2 = Z; screen direction (Y down) and depth (negative = towards the viewer).</summary>
    public readonly record struct GizmoAxis(int Axis, double X, double Y, double Depth);

    /// <summary>One line of the cut list: how many boards of this name and size to cut.</summary>
    public sealed record CutRow(string Name, int Quantity, double Length, double Width, double Thickness, IReadOnlyList<string> PartNames);

    public static class CutList
    {
        private static readonly Regex Numbered = new(@"\s+\d+$", RegexOptions.Compiled);

        /// <summary>Boards with the same name (numbers ignored) and the same size - whichever way round they lie - are one row.</summary>
        public static IReadOnlyList<CutRow> From(PieceModel model)
        {
            var rows = new List<CutRow>();
            foreach (var group in model.Parts.GroupBy(Key))
            {
                var first = group.First();
                var d = Dims(first);
                rows.Add(new CutRow(Numbered.Replace(first.Name, ""), group.Count(), d[0], d[1], d[2], group.Select(p => p.Name).ToList()));
            }
            return rows;
        }

        public static double TotalVolumeMm3(PieceModel model) => model.Parts.Sum(p => p.Size.X * p.Size.Y * p.Size.Z);

        private static double[] Dims(Part p) => new[] { p.Size.X, p.Size.Y, p.Size.Z }.OrderByDescending(d => d).ToArray();

        private static string Key(Part p)
        {
            var d = Dims(p);
            string cuts = string.Join(",", p.Bevels.Select(b => $"{b.FaceA}{b.FaceB}:{b.Angle:0.#}:{b.Depth:0.#}"));
            return $"{Numbered.Replace(p.Name, "")}|{d[0]:0.###}|{d[1]:0.###}|{d[2]:0.###}|{cuts}";
        }
    }

    /// <summary>
    /// The camera of the 3D view, as maths. It orbits a target; the WPF camera is set from the same numbers, and the dimension
    /// lines are placed with <see cref="Project"/>, so the overlay and the picture can never disagree.
    /// </summary>
    public sealed class OrbitCamera
    {
        public const double MinPitch = -5;
        public const double MaxPitch = 85;
        public const double MinDistance = 100;
        public const double MaxDistance = 60000;
        private const double Near = 1;

        /// <summary>Degrees turned round the target; 0 looks at the front (+Z side) of a piece.</summary>
        public double Yaw { get; set; } = 35;

        /// <summary>Degrees above the floor.</summary>
        public double Pitch { get; set; } = 22;

        public double Distance { get; set; } = 3000;

        public Vec3 Target { get; set; } = Vec3.Zero;

        /// <summary>The horizontal field of view, as WPF's PerspectiveCamera takes it.</summary>
        public double FovDegrees { get; set; } = 45;

        private static double Rad(double degrees) => degrees * Math.PI / 180;

        public Vec3 Position
        {
            get
            {
                double p = Rad(Pitch), y = Rad(Yaw);
                return Target + new Vec3(Math.Cos(p) * Math.Sin(y), Math.Sin(p), Math.Cos(p) * Math.Cos(y)) * Distance;
            }
        }

        public Vec3 LookDirection => Target - Position;

        public void Orbit(double deltaYaw, double deltaPitch)
        {
            Yaw += deltaYaw;
            Pitch = Math.Clamp(Pitch + deltaPitch, MinPitch, MaxPitch);
        }

        public void Zoom(double factor) => Distance = Math.Clamp(Distance * factor, MinDistance, MaxDistance);

        /// <summary>Slides the target sideways and up/down the screen, by pixels dragged.</summary>
        public void Pan(double dxPixels, double dyPixels, double viewportWidth)
        {
            if (viewportWidth <= 0) return;
            var (right, up, _) = Basis();
            double perPixel = 2 * Distance * Math.Tan(Rad(FovDegrees) / 2) / viewportWidth;
            Target = Target - right * (dxPixels * perPixel) + up * (dyPixels * perPixel);
        }

        /// <summary>
        /// The X, Y and Z axes as the corner gizmo draws them: where each unit axis points on screen (X right, Y down, length 1 when
        /// it lies flat on the screen) and how far it points away from the viewer (negative = towards you, drawn on top).
        /// </summary>
        public IReadOnlyList<GizmoAxis> GizmoAxes()
        {
            var (right, up, forward) = Basis();
            var axes = new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) };
            return axes.Select((a, i) => new GizmoAxis(i, Vec3.Dot(a, right), -Vec3.Dot(a, up), Vec3.Dot(a, forward))).ToList();
        }

        /// <summary>Turns the view to look at the piece from one side: from +X / -X, from above / below (as far as the pitch goes), or from +Z / -Z.</summary>
        public void LookFrom(int axis, bool positive)
        {
            switch (axis)
            {
                case 0: Yaw = positive ? 90 : -90; Pitch = 0; break;
                case 1: Pitch = positive ? MaxPitch : MinPitch; break;
                case 2: Yaw = positive ? 0 : 180; Pitch = 0; break;
            }
        }

        private (Vec3 Right, Vec3 Up, Vec3 Forward) Basis()
        {
            var forward = LookDirection.Normalized;
            var right = Vec3.Cross(forward, new Vec3(0, 1, 0)).Normalized;
            var up = Vec3.Cross(right, forward);
            return (right, up, forward);
        }

        /// <summary>The line of sight through a pixel of a view of this size (the opposite of <see cref="Project"/>).</summary>
        public Ray3 Ray(double x, double y, double width, double height)
        {
            var (right, up, forward) = Basis();
            double tanH = Math.Tan(Rad(FovDegrees) / 2);
            double tanV = tanH * height / width;
            double ndcX = (x / width - 0.5) * 2, ndcY = (0.5 - y / height) * 2;
            var direction = (forward + right * (ndcX * tanH) + up * (ndcY * tanV)).Normalized;
            return new Ray3(Position, direction);
        }

        /// <summary>How many millimetres one pixel is at the distance of the target.</summary>
        public double MmPerPixel(double width) => width <= 0 ? 1 : 2 * Distance * Math.Tan(Rad(FovDegrees) / 2) / width;

        /// <summary>Where a point lands on a view of this size, or null when it is behind the camera.</summary>
        public ScreenPoint? Project(Vec3 point, double width, double height)
        {
            var (right, up, forward) = Basis();
            var d = point - Position;
            double depth = Vec3.Dot(d, forward);
            if (depth <= Near) return null;
            double tanH = Math.Tan(Rad(FovDegrees) / 2);
            double tanV = tanH * height / width;
            double ndcX = Vec3.Dot(d, right) / depth / tanH;
            double ndcY = Vec3.Dot(d, up) / depth / tanV;
            return new ScreenPoint((ndcX * 0.5 + 0.5) * width, (0.5 - ndcY * 0.5) * height);
        }

        /// <summary>Aims at the middle of the box and backs off until all of it is in view with a margin.</summary>
        public void FitTo(Vec3 min, Vec3 size, double width, double height)
        {
            Target = min + size * 0.5;
            const double margin = 0.07;
            bool Fits(double distance)
            {
                Distance = distance;
                for (int i = 0; i < 8; i++)
                {
                    var corner = min + new Vec3((i & 1) * size.X, (i >> 1 & 1) * size.Y, (i >> 2 & 1) * size.Z);
                    if (Project(corner, width, height) is not { } p) return false;
                    if (p.X < width * margin || p.X > width * (1 - margin) || p.Y < height * margin || p.Y > height * (1 - margin)) return false;
                }
                return true;
            }

            double low = MinDistance, high = MaxDistance;
            for (int i = 0; i < 50; i++)
            {
                double mid = (low + high) / 2;
                if (Fits(mid)) high = mid;
                else low = mid;
            }
            Distance = Math.Clamp(high, MinDistance, MaxDistance);
        }
    }
}
