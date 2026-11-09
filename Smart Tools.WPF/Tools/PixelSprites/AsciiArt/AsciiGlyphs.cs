using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>How much ink one character puts in its cell: the whole cell and its four quarters (top-left, top-right, bottom-left, bottom-right), all 0..1.</summary>
    public readonly record struct AsciiCharStat(char Ch, float Total, float Q0, float Q1, float Q2, float Q3);

    /// <summary>
    /// One monospace font as the ASCII tool uses it: the size of a character cell
    /// (so the picture is not stretched), glyph lookup, glyph runs for drawing, and
    /// the real ink coverage of each character, measured by drawing it. Measuring
    /// needs WPF drawing, so call <see cref="Measure"/> on an STA thread.
    /// </summary>
    public sealed class AsciiGlyphs
    {
        private static readonly string[] Candidates = { "Consolas", "Cascadia Mono", "Cascadia Code", "Courier New", "Lucida Console" };

        public string FontName { get; }
        private readonly GlyphTypeface _glyphs;

        /// <summary>Cell width and height for a font size of 1 (multiply by the size).</summary>
        public double CellWidthEm { get; }
        public double CellHeightEm { get; }
        public double BaselineEm { get; }

        /// <summary>Width of a cell divided by its height; a picture's rows are this much fewer than its columns.</summary>
        public double CellAspect => CellWidthEm / CellHeightEm;

        private AsciiGlyphs(string name, GlyphTypeface glyphs, double advanceEm, double heightEm, double baselineEm)
        {
            FontName = name;
            _glyphs = glyphs;
            CellWidthEm = advanceEm;
            CellHeightEm = heightEm;
            BaselineEm = baselineEm;
        }

        /// <summary>The monospace fonts from the shortlist that this PC has.</summary>
        public static List<string> InstalledFonts()
        {
            var have = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
            var list = Candidates.Where(have.Contains).ToList();
            if (list.Count == 0) list.Add("Courier New");
            return list;
        }

        private static readonly Dictionary<string, AsciiGlyphs> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static AsciiGlyphs Create(string fontName)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(fontName, out var hit)) return hit;
                var made = TryBuild(fontName) ?? TryBuild("Courier New") ?? TryBuild("Consolas")
                    ?? throw new InvalidOperationException("No monospace font could be loaded.");
                Cache[fontName] = made;
                return made;
            }
        }

        private static AsciiGlyphs? TryBuild(string name)
        {
            try
            {
                var typeface = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (!typeface.TryGetGlyphTypeface(out var glyphs)) return null;
                if (!glyphs.CharacterToGlyphMap.TryGetValue('M', out ushort m)) return null;
                double advance = glyphs.AdvanceWidths[m];
                // Line height and baseline from FormattedText (what WPF text itself uses), measured at a large size.
                var ft = new FormattedText("M", System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    typeface, 100, Brushes.Black, 1.0);
                if (advance <= 0 || ft.Height <= 0) return null;
                return new AsciiGlyphs(name, glyphs, advance, ft.Height / 100.0, ft.Baseline / 100.0);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or NotSupportedException)
            {
                return null;
            }
        }

        public bool Has(char c) => _glyphs.CharacterToGlyphMap.ContainsKey(c);

        private ushort IndexOf(char c) =>
            _glyphs.CharacterToGlyphMap.TryGetValue(c, out ushort i) ? i
            : _glyphs.CharacterToGlyphMap.TryGetValue(' ', out ushort sp) ? sp : (ushort)0;

        /// <summary>A run of characters on one line, each in its own cell. <c>x</c>/<c>y</c> are the top left of the first cell.</summary>
        public GlyphRun Run(ReadOnlySpan<char> text, double em, double x, double y)
        {
            var idx = new ushort[text.Length];
            var adv = new double[text.Length];
            double cell = CellWidthEm * em;
            for (int i = 0; i < text.Length; i++)
            {
                idx[i] = IndexOf(text[i]);
                adv[i] = cell;
            }
            return new GlyphRun(_glyphs, 0, false, em, 1.0f, idx, new Point(x, y + BaselineEm * em), adv, null, null, null, null, null, null);
        }

        /// <summary>
        /// The ink coverage of each character, measured at a fixed size. Run on an STA
        /// thread (it draws). The result is cached per font and character list.
        /// </summary>
        public AsciiCharStat[] Measure(IReadOnlyList<char> chars)
        {
            const double em = 40;
            int w = Math.Max(2, (int)Math.Ceiling(CellWidthEm * em)), h = Math.Max(2, (int)Math.Ceiling(CellHeightEm * em));
            var result = new AsciiCharStat[chars.Count];
            var pixels = new byte[w * h * 4];
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            for (int n = 0; n < chars.Count; n++)
            {
                char c = chars[n];
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
                    if (c != ' ') dc.DrawGlyphRun(Brushes.White, Run(new[] { c }, em, 0, 0));
                }
                bitmap.Clear();
                bitmap.Render(visual);
                bitmap.CopyPixels(pixels, w * 4, 0);

                double[] q = new double[4];
                int[] count = new int[4];
                for (int y = 0; y < h; y++)
                {
                    int row = y < h / 2 ? 0 : 2;
                    for (int x = 0; x < w; x++)
                    {
                        int quarter = row + (x < w / 2 ? 0 : 1);
                        q[quarter] += pixels[(y * w + x) * 4 + 1] / 255.0;   // green channel of white-on-black = coverage
                        count[quarter]++;
                    }
                }
                for (int i = 0; i < 4; i++) q[i] = count[i] == 0 ? 0 : q[i] / count[i];
                result[n] = new AsciiCharStat(c, (float)((q[0] + q[1] + q[2] + q[3]) / 4), (float)q[0], (float)q[1], (float)q[2], (float)q[3]);
            }
            return result;
        }
    }
}
