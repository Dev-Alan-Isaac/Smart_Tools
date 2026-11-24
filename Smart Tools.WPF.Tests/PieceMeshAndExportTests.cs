using System.Globalization;
using System.Text;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Tests
{
    public class BoxMeshTests
    {
        private static readonly Part Board = new("Board", new Vec3(10, 20, 30), new Vec3(400, 18, 250), PartKind.Board);

        /// <summary>Sum of signed tetrahedra to the origin: the volume when the surface is closed and faces outward.</summary>
        private static double SignedVolume(IEnumerable<Triangle> triangles) =>
            triangles.Sum(t => Vec3.Dot(t.A, Vec3.Cross(t.B, t.C)) / 6.0);

        [Fact]
        public void A_box_is_twelve_triangles() => Assert.Equal(12, BoxMesh.Triangles(Board).Count);

        [Fact]
        public void The_surface_is_closed_and_faces_outward_so_the_volume_is_the_box_volume() =>
            Assert.Equal(400 * 18 * 250, SignedVolume(BoxMesh.Triangles(Board)), 6);

        [Fact]
        public void Normals_are_unit_length_and_point_away_from_the_centre()
        {
            var centre = Board.Min + Board.Size * 0.5;
            foreach (var t in BoxMesh.Triangles(Board))
            {
                Assert.Equal(1.0, t.Normal.Length, 9);
                var middle = (t.A + t.B + t.C) * (1.0 / 3.0);
                Assert.True(Vec3.Dot(t.Normal, middle - centre) > 0);
            }
        }

        [Fact]
        public void Every_corner_lies_on_the_box()
        {
            foreach (var t in BoxMesh.Triangles(Board))
                foreach (var v in new[] { t.A, t.B, t.C })
                {
                    Assert.InRange(v.X, Board.Min.X, Board.Max.X);
                    Assert.InRange(v.Y, Board.Min.Y, Board.Max.Y);
                    Assert.InRange(v.Z, Board.Min.Z, Board.Max.Z);
                }
        }

        [Fact]
        public void The_triangle_normal_agrees_with_the_winding()
        {
            foreach (var t in BoxMesh.Triangles(Board))
            {
                var fromWinding = Vec3.Cross(t.B - t.A, t.C - t.A).Normalized;
                Assert.Equal(1.0, Vec3.Dot(fromWinding, t.Normal), 9);
            }
        }
    }

    public class PieceExportTests
    {
        private static PieceModel Sample() => new("Sample",
            new[]
            {
                new Part("Top", new Vec3(0, 700, 0), new Vec3(1000, 30, 600), PartKind.Top),
                new Part("Leg 1", new Vec3(20, 0, 20), new Vec3(60, 700, 60), PartKind.Leg),
            });

        // ---------------------------------------------------------------- OBJ
        [Fact]
        public void Obj_has_eight_vertices_and_six_faces_per_part()
        {
            string obj = PieceExport.ToObj(Sample());
            var lines = obj.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            Assert.Equal(16, lines.Count(l => l.StartsWith("v ")));
            Assert.Equal(12, lines.Count(l => l.StartsWith("f ")));
        }

        [Fact]
        public void Obj_names_every_part_as_a_group()
        {
            string obj = PieceExport.ToObj(Sample());
            Assert.Contains("g Top", obj);
            Assert.Contains("g Leg_1", obj);   // spaces would split the name in some readers
        }

        [Fact]
        public void Obj_says_its_units_and_uses_invariant_numbers()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");
            try
            {
                string obj = PieceExport.ToObj(new PieceModel("x", new[] { new Part("A", new Vec3(0.5, 0, 0), new Vec3(1.25, 2, 3), PartKind.Board) }));
                Assert.Contains("millimetres", obj);
                Assert.Contains("v 0.5 0 0", obj);
                Assert.DoesNotContain(",", obj.Replace("# ", "").Split('\n').Where(l => l.StartsWith("v ")).Aggregate("", (a, b) => a + b));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Obj_face_indices_point_at_real_vertices()
        {
            string obj = PieceExport.ToObj(Sample());
            int vertices = obj.Split('\n').Count(l => l.StartsWith("v "));
            int normals = obj.Split('\n').Count(l => l.StartsWith("vn "));
            foreach (var face in obj.Split('\n').Where(l => l.StartsWith("f ")))
                foreach (var corner in face.Trim().Split(' ').Skip(1))
                {
                    var parts = corner.Split("//");
                    Assert.InRange(int.Parse(parts[0]), 1, vertices);
                    Assert.InRange(int.Parse(parts[1]), 1, normals);
                }
        }

        [Fact]
        public void Obj_keeps_the_model_upright_y_is_up()
        {
            string obj = PieceExport.ToObj(Sample());
            double maxY = obj.Split('\n').Where(l => l.StartsWith("v ")).Max(l => double.Parse(l.Trim().Split(' ')[2], CultureInfo.InvariantCulture));
            Assert.Equal(730, maxY, 6);
        }

        // ---------------------------------------------------------------- STL
        private static (double Volume, double MinZ, double MaxZ, int Triangles) ReadStl(byte[] bytes)
        {
            int count = (int)BitConverter.ToUInt32(bytes, 80);
            double volume = 0, minZ = double.MaxValue, maxZ = double.MinValue;
            for (int i = 0; i < count; i++)
            {
                int at = 84 + 50 * i + 12;
                var v = new Vec3[3];
                for (int k = 0; k < 3; k++)
                    v[k] = new Vec3(BitConverter.ToSingle(bytes, at + 12 * k), BitConverter.ToSingle(bytes, at + 12 * k + 4), BitConverter.ToSingle(bytes, at + 12 * k + 8));
                volume += Vec3.Dot(v[0], Vec3.Cross(v[1], v[2])) / 6.0;
                foreach (var p in v) { minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); }
            }
            return (volume, minZ, maxZ, count);
        }

        [Fact]
        public void Stl_is_a_binary_file_of_the_right_length()
        {
            byte[] stl = PieceExport.ToStlBinary(Sample());
            Assert.Equal(84 + 50 * 24, stl.Length);
            Assert.Equal(24u, BitConverter.ToUInt32(stl, 80));
        }

        [Fact]
        public void Stl_header_is_eighty_bytes_and_does_not_start_with_solid()
        {
            byte[] stl = PieceExport.ToStlBinary(Sample());
            Assert.NotEqual("solid", Encoding.ASCII.GetString(stl, 0, 5));   // a "solid" header makes some readers think it is ASCII
        }

        [Fact]
        public void Stl_stands_the_piece_up_z_is_up_for_printers()
        {
            var stl = ReadStl(PieceExport.ToStlBinary(Sample()));
            Assert.Equal(0, stl.MinZ, 3);
            Assert.Equal(730, stl.MaxZ, 3);
        }

        [Fact]
        public void Stl_is_closed_and_faces_outward_so_its_volume_is_the_sum_of_the_boards()
        {
            var stl = ReadStl(PieceExport.ToStlBinary(Sample()));
            double expected = 1000.0 * 30 * 600 + 60.0 * 700 * 60;
            Assert.InRange(stl.Volume, expected * (1 - 1e-5), expected * (1 + 1e-5));   // the file stores 32-bit floats
        }

        [Fact]
        public void Stl_of_an_empty_piece_is_just_the_header()
        {
            byte[] stl = PieceExport.ToStlBinary(new PieceModel("none", Array.Empty<Part>()));
            Assert.Equal(84, stl.Length);
        }
    }
}
