using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// The colour of one board: the whole board, and optionally single faces on top of it (faces 0 left, 1 right, 2 bottom, 3 top,
    /// 4 back, 5 front). Colours are "#RRGGBB"; null = the wood tone of the board's kind.
    /// </summary>
    public sealed class BoardPaint
    {
        public string? Color { get; set; }

        /// <summary>Face index -> colour. Null or empty when no face has its own colour.</summary>
        public Dictionary<int, string>? Faces { get; set; }
    }

    /// <summary>One ready-made colour of the picker: its language key suffix and its colour.</summary>
    public readonly record struct PaintSwatch(string Id, string Hex);
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Colours chosen for boards, kept apart from the piece itself (a preset is rebuilt from its sliders every time, so the colour is
    /// looked up by board name). Pure: no WPF.
    /// </summary>
    public static class PiecePaint
    {
        public static IReadOnlyList<PaintSwatch> Swatches { get; } = new PaintSwatch[]
        {
            new("Pine", "#E3C18B"), new("Oak", "#C8A165"), new("Walnut", "#6B4226"),
            new("White", "#F2F2F2"), new("Grey", "#8C8C8C"), new("Black", "#262626"),
            new("Red", "#C0392B"), new("Orange", "#E67E22"), new("Yellow", "#F1C40F"),
            new("Green", "#27AE60"), new("Blue", "#2E86DE"), new("Navy", "#1F3A5F"),
        };

        /// <summary>"#RRGGBB" (or "RRGGBB") as three bytes; false for anything else.</summary>
        public static bool TryParse(string? hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            var s = hex.Trim().TrimStart('#');
            if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int v)) return false;
            r = (byte)(v >> 16);
            g = (byte)(v >> 8);
            b = (byte)v;
            return true;
        }

        public static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

        /// <summary>A colour written the one way it is stored, or null when it is not a colour.</summary>
        public static string? Normalize(string? hex) => TryParse(hex, out byte r, out byte g, out byte b) ? Hex(r, g, b) : null;

        /// <summary>
        /// The face a triangle of the board's surface lies on, or null for surfaces that are not one of the six sides (the slope of a
        /// bevel, the wall of a hole): those take the board's colour.
        /// </summary>
        public static int? FaceOf(Part part, Triangle t, double eps = 1e-6)
        {
            var n = t.Normal;
            int axis; bool positive;
            if (Math.Abs(n.X) > 0.999) { axis = 0; positive = n.X > 0; }
            else if (Math.Abs(n.Y) > 0.999) { axis = 1; positive = n.Y > 0; }
            else if (Math.Abs(n.Z) > 0.999) { axis = 2; positive = n.Z > 0; }
            else return null;
            double plane = Get(positive ? part.Max : part.Min, axis);
            foreach (var c in new[] { t.A, t.B, t.C })
                if (Math.Abs(Get(c, axis) - plane) > eps) return null;   // an axis-facing hole wall inside the board
            return axis * 2 + (positive ? 1 : 0);
        }

        /// <summary>The side of the board nearest to a point on (or near) its surface, for "colour the face I clicked".</summary>
        public static int FaceAt(Part part, Vec3 point)
        {
            int best = 0;
            double bestDistance = double.PositiveInfinity;
            for (int face = 0; face < 6; face++)
            {
                int axis = face / 2;
                double plane = Get(face % 2 == 1 ? part.Max : part.Min, axis);
                double d = Math.Abs(Get(point, axis) - plane);
                if (d < bestDistance) { bestDistance = d; best = face; }
            }
            return best;
        }

        /// <summary>The colour of a board's face (its own colour, else the board's), or null for the wood tone.</summary>
        public static string? ColourOf(IReadOnlyDictionary<string, BoardPaint>? paints, string board, int? face)
        {
            if (paints == null || !paints.TryGetValue(board, out var paint)) return null;
            if (face is int f && paint.Faces != null && paint.Faces.TryGetValue(f, out var own)) return own;
            return paint.Color;
        }

        /// <summary>Paints the whole board (its single faces too); null takes the board back to wood.</summary>
        public static void PaintBoard(IDictionary<string, BoardPaint> paints, string board, string? hex)
        {
            hex = Normalize(hex);
            if (hex == null) paints.Remove(board);
            else paints[board] = new BoardPaint { Color = hex };
        }

        /// <summary>Paints one face of a board; null gives that face back the board's colour.</summary>
        public static void PaintFace(IDictionary<string, BoardPaint> paints, string board, int face, string? hex)
        {
            if (face < 0 || face > 5) return;
            hex = Normalize(hex);
            if (!paints.TryGetValue(board, out var paint))
            {
                if (hex == null) return;
                paints[board] = paint = new BoardPaint();
            }
            paint.Faces ??= new Dictionary<int, string>();
            if (hex == null) paint.Faces.Remove(face);
            else paint.Faces[face] = hex;
            if (paint.Faces.Count == 0) paint.Faces = null;
            if (paint.Color == null && paint.Faces == null) paints.Remove(board);
        }

        /// <summary>
        /// Drops anything that is not a colour (a hand-edited file). Colours of boards the piece does not have right now are kept on
        /// purpose: a door turned off with the Doors slider gets its colour back when it returns.
        /// </summary>
        public static void Sanitize(IDictionary<string, BoardPaint> paints)
        {
            foreach (var name in paints.Keys.ToList())
            {
                var paint = paints[name];
                if (paint == null) { paints.Remove(name); continue; }
                paint.Color = Normalize(paint.Color);
                if (paint.Faces != null)
                {
                    paint.Faces = paint.Faces.Where(kv => kv.Key is >= 0 and <= 5 && Normalize(kv.Value) != null)
                        .ToDictionary(kv => kv.Key, kv => Normalize(kv.Value)!);
                    if (paint.Faces.Count == 0) paint.Faces = null;
                }
                if (paint.Color == null && paint.Faces == null) paints.Remove(name);
            }
        }

        private static double Get(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    }
}
