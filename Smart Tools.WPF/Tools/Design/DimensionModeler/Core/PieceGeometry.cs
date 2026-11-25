using System.Globalization;
using System.IO;
using System.Text;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The one description of a board's surface: the preview, the OBJ and the STL are all made from it.</summary>
    public static class BoxMesh
    {
        /// <summary>The eight corners in the order OBJ uses; bit 0 = x, bit 1 = y, bit 2 = z of the far side... see <see cref="Corner"/>.</summary>
        public static Vec3 Corner(Part part, int index)
        {
            // 0:(x0,y0,z0) 1:(x1,y0,z0) 2:(x1,y1,z0) 3:(x0,y1,z0) 4:(x0,y0,z1) 5:(x1,y0,z1) 6:(x1,y1,z1) 7:(x0,y1,z1)
            var a = part.Min;
            var b = part.Max;
            return index switch
            {
                0 => new Vec3(a.X, a.Y, a.Z),
                1 => new Vec3(b.X, a.Y, a.Z),
                2 => new Vec3(b.X, b.Y, a.Z),
                3 => new Vec3(a.X, b.Y, a.Z),
                4 => new Vec3(a.X, a.Y, b.Z),
                5 => new Vec3(b.X, a.Y, b.Z),
                6 => new Vec3(b.X, b.Y, b.Z),
                _ => new Vec3(a.X, b.Y, b.Z),
            };
        }

        /// <summary>The six faces as corner indices, each wound counter-clockwise seen from outside, with their outward normal.</summary>
        public static readonly (int[] Corners, Vec3 Normal)[] Faces =
        {
            (new[] { 1, 2, 6, 5 }, new Vec3(1, 0, 0)),     // +X
            (new[] { 0, 4, 7, 3 }, new Vec3(-1, 0, 0)),    // -X
            (new[] { 3, 7, 6, 2 }, new Vec3(0, 1, 0)),     // +Y
            (new[] { 0, 1, 5, 4 }, new Vec3(0, -1, 0)),    // -Y
            (new[] { 4, 5, 6, 7 }, new Vec3(0, 0, 1)),     // +Z
            (new[] { 0, 3, 2, 1 }, new Vec3(0, 0, -1)),    // -Z
        };

        public static IReadOnlyList<Triangle> Triangles(Part part)
        {
            if (part.Bevels.Count > 0) return PieceBevels.Triangles(part);
            if (part.Holes.Count > 0) return PieceHoles.Triangles(part);
            var list = new List<Triangle>(12);
            foreach (var (corners, normal) in Faces)
            {
                var a = Corner(part, corners[0]);
                var b = Corner(part, corners[1]);
                var c = Corner(part, corners[2]);
                var d = Corner(part, corners[3]);
                list.Add(new Triangle(a, b, c, normal));
                list.Add(new Triangle(a, c, d, normal));
            }
            return list;
        }
    }

    /// <summary>The ways a piece leaves the program as a file. Pure: the caller decides where the text or bytes go.</summary>
    public static class PieceExport
    {
        private static string Num(double value)
        {
            string s = value.ToString("0.###", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        /// <summary>Wavefront OBJ: one group per board, quads, flat normals, Y up, millimetres.</summary>
        public static string ToObj(PieceModel model)
        {
            var sb = new StringBuilder();
            sb.Append("# Smart Tools - Dimension Modeler\n");
            sb.Append("# ").Append(model.Name).Append('\n');
            sb.Append("# units: millimetres (1 unit = 1 mm), Y is up\n");
            foreach (var (_, normal) in BoxMesh.Faces)
                sb.Append("vn ").Append(Num(normal.X)).Append(' ').Append(Num(normal.Y)).Append(' ').Append(Num(normal.Z)).Append('\n');

            int offset = 0;
            foreach (var part in model.Parts)
            {
                sb.Append("g ").Append(part.Name.Replace(' ', '_')).Append('\n');
                if (part.Holes.Count > 0 || part.Bevels.Count > 0)
                {
                    // a board with holes or angled cuts: plain triangles, the viewer works out the normals
                    var triangles = BoxMesh.Triangles(part);
                    foreach (var t in triangles)
                        foreach (var v in new[] { t.A, t.B, t.C })
                            sb.Append("v ").Append(Num(v.X)).Append(' ').Append(Num(v.Y)).Append(' ').Append(Num(v.Z)).Append('\n');
                    for (int i = 0; i < triangles.Count; i++)
                        sb.Append("f ").Append(offset + 3 * i + 1).Append(' ').Append(offset + 3 * i + 2).Append(' ').Append(offset + 3 * i + 3).Append('\n');
                    offset += 3 * triangles.Count;
                    continue;
                }
                for (int i = 0; i < 8; i++)
                {
                    var c = BoxMesh.Corner(part, i);
                    sb.Append("v ").Append(Num(c.X)).Append(' ').Append(Num(c.Y)).Append(' ').Append(Num(c.Z)).Append('\n');
                }
                for (int f = 0; f < BoxMesh.Faces.Length; f++)
                {
                    sb.Append('f');
                    foreach (int corner in BoxMesh.Faces[f].Corners)
                        sb.Append(' ').Append(offset + corner + 1).Append("//").Append(f + 1);
                    sb.Append('\n');
                }
                offset += 8;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Binary STL, Z up (what printers and slicers expect) and moved so the piece starts at 0, 0, 0.
        /// The model's Y (up) becomes Z; its Z (depth) becomes -Y, which keeps the surface facing outward.
        /// </summary>
        public static byte[] ToStlBinary(PieceModel model)
        {
            var triangles = model.Parts.SelectMany(BoxMesh.Triangles).ToList();
            var min = model.Min;
            var max = model.Max;

            Vec3 Up(Vec3 p) => new(p.X - min.X, max.Z - p.Z, p.Y - min.Y);

            using var stream = new MemoryStream(84 + 50 * triangles.Count);
            using var writer = new BinaryWriter(stream);
            var header = new byte[80];
            Encoding.ASCII.GetBytes("Smart Tools Dimension Modeler (millimetres)").CopyTo(header, 0);
            writer.Write(header);
            writer.Write((uint)triangles.Count);
            foreach (var t in triangles)
            {
                var a = Up(t.A);
                var b = Up(t.B);
                var c = Up(t.C);
                var n = Vec3.Cross(b - a, c - a).Normalized;
                foreach (var v in new[] { n, a, b, c })
                {
                    writer.Write((float)v.X);
                    writer.Write((float)v.Y);
                    writer.Write((float)v.Z);
                }
                writer.Write((ushort)0);
            }
            writer.Flush();
            return stream.ToArray();
        }
    }
}
