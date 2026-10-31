using PdfSharp.Fonts;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// PdfSharp's Core build asks this whenever it needs a physical font for
    /// an XFont. This app only ever requests one family (regular or bold,
    /// for the title page and page-number footer), so rather than pulling
    /// in a whole embedded-font or cross-platform font-matching library,
    /// this maps those two requests straight to .ttf files Windows ships in
    /// the Fonts folder.
    ///
    /// Verdana is the preferred choice, but isn't guaranteed to be installed
    /// - Server Core, some locked-down enterprise images, or a font someone
    /// simply removed can all leave it missing. Rather than let every single
    /// image in a batch fail with a raw FileNotFoundException the moment the
    /// first XFont is constructed, this falls back through a short list of
    /// near-universally-available Windows fonts, and only throws (with a
    /// clear, actionable message) if none of them are present either.
    /// </summary>
    internal sealed class WindowsFontResolver : IFontResolver
    {
        private static readonly string FontsDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        // Checked in order; the first family with a regular face file
        // present wins. Bold is used if that same family's bold file is
        // also present, otherwise PdfSharp simulates bold from the regular
        // face instead of failing outright.
        private static readonly (string Family, string Regular, string Bold)[] FallbackFonts =
        {
            ("Verdana", "verdana.ttf", "verdanab.ttf"),
            ("Arial", "arial.ttf", "arialbd.ttf"),
            ("Tahoma", "tahoma.ttf", "tahomabd.ttf"),
            ("Segoe UI", "segoeui.ttf", "segoeuib.ttf"),
        };

        private static readonly Lazy<(string Family, string Regular, string? Bold)> Resolved =
            new(FindAvailableFont);

        private static (string Family, string Regular, string? Bold) FindAvailableFont()
        {
            foreach (var (family, regular, bold) in FallbackFonts)
            {
                if (!File.Exists(Path.Combine(FontsDirectory, regular)))
                    continue;

                bool hasBold = File.Exists(Path.Combine(FontsDirectory, bold));
                return (family, regular, hasBold ? bold : null);
            }

            string checkedFamilies = string.Join(", ", FallbackFonts.Select(f => f.Family));
            throw new FileNotFoundException(
                $"PDF creation needs at least one of these fonts installed: {checkedFamilies} " +
                $"(checked {FontsDirectory}). None were found.");
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var (_, _, bold) = Resolved.Value;

            string faceName = isBold && bold != null ? "Bold" : "Regular";

            // If we do have a real bold face file, PdfSharp doesn't need to
            // simulate bold by skewing/thickening glyphs itself.
            bool mustSimulateBold = isBold && bold == null;

            return new FontResolverInfo(faceName, mustSimulateBold, mustSimulateItalic: isItalic);
        }

        public byte[] GetFont(string faceName)
        {
            var (_, regular, bold) = Resolved.Value;
            string fileName = string.Equals(faceName, "Bold", StringComparison.OrdinalIgnoreCase) && bold != null
                ? bold
                : regular;

            string path = Path.Combine(FontsDirectory, fileName);

            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FileNotFoundException(
                    $"Couldn't read the font file needed for PDF creation ({path}): {ex.Message}", ex);
            }
        }
    }
}