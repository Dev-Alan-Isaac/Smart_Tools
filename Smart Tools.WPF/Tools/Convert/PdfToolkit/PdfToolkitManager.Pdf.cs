using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Smart_Tools.WPF.Models;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The PdfSharp (and ImageMagick) side of the PDF Toolkit.</summary>
    public static partial class PdfToolkitManager
    {
        /// <summary>The real engine the view uses.</summary>
        public static readonly IPdfEngine Engine = new PdfSharpEngine();

        /// <summary>Page count for the list (read off the UI thread, rows on screen only). Throws when the PDF can't be opened.</summary>
        public static int CountPages(string path)
        {
            using var source = Engine.Open(path);
            return source.PageCount;
        }

        private sealed class PdfSharpSource : IPdfSource
        {
            public PdfSharpSource(PdfDocument document) => Document = document;

            public PdfDocument Document { get; }
            public int PageCount => Document.PageCount;
            public void Dispose() => Document.Dispose();
        }

        private sealed class PdfSharpWriter : IPdfWriter
        {
            private readonly PdfDocument _doc = new();

            private int _pagesAtSave = -1;

            /// <summary>PdfSharp refuses PageCount once the document is saved, so the count is kept when saving.</summary>
            public int PageCount => _pagesAtSave >= 0 ? _pagesAtSave : _doc.PageCount;

            public void AddPages(IPdfSource source, IReadOnlyList<int> pages, string? bookmark)
            {
                var from = ((PdfSharpSource)source).Document;
                PdfPage? first = null;
                foreach (int p in pages)
                {
                    var added = _doc.AddPage(from.Pages[p - 1]);
                    first ??= added;
                }
                // Merged files lose their own bookmarks (PdfSharp copies pages only), so mark where each one starts.
                if (bookmark != null && first != null) _doc.Outlines.Add(bookmark, first, true);
            }

            public void Save(string path)
            {
                _doc.Options.CompressContentStreams = true;
                _pagesAtSave = _doc.PageCount;
                _doc.Save(path);
            }

            public void Dispose() => _doc.Dispose();
        }

        private sealed class PdfSharpEngine : IPdfEngine
        {
            public IPdfSource Open(string path) => new PdfSharpSource(PdfReader.Open(path, PdfDocumentOpenMode.Import));

            public IPdfWriter NewDocument() => new PdfSharpWriter();

            /// <summary>
            /// The old Compress only re-saved the file, which rarely saves
            /// anything: the space in most big PDFs is photos and scans. This
            /// re-compresses JPEG pictures (smaller and at a lower quality,
            /// per <see cref="CompressPreset"/>) and squeezes page contents.
            /// Only plain 8-bit RGB or grey JPEGs are touched; CMYK, masks,
            /// inverted or unusual pictures stay exactly as they are. A new
            /// picture is only swapped in when it's at least 10 % smaller
            /// and decodes back with the same colours.
            /// </summary>
            public PdfCompressOutcome Compress(string source, string target, PdfCompressLevel level, CancellationToken token)
            {
                var (quality, maxSide) = CompressPreset(level);
                using var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
                var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                int recompressed = 0, kept = 0;

                foreach (PdfPage page in doc.Pages)
                {
                    token.ThrowIfCancellationRequested();
                    WalkResources(Resolve(page.Elements["/Resources"]) as PdfDictionary, 0);
                }

                doc.Options.NoCompression = false;
                doc.Options.CompressContentStreams = true;
                doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
                token.ThrowIfCancellationRequested();
                doc.Save(target);
                return new PdfCompressOutcome(recompressed, kept);

                void WalkResources(PdfDictionary? resources, int depth)
                {
                    if (resources == null || depth > 8) return;
                    if (Resolve(resources.Elements["/XObject"]) is not PdfDictionary xobjects) return;
                    foreach (string key in xobjects.Elements.Keys.ToList())
                    {
                        token.ThrowIfCancellationRequested();
                        if (Resolve(xobjects.Elements[key]) is not PdfDictionary obj || !seen.Add(obj)) continue;
                        string subtype = obj.Elements.GetName("/Subtype");
                        if (subtype == "/Image")
                        {
                            if (TryRecompress(obj, quality, maxSide)) recompressed++;
                            else kept++;
                        }
                        else if (subtype == "/Form")
                        {
                            WalkResources(Resolve(obj.Elements["/Resources"]) as PdfDictionary, depth + 1);
                        }
                    }
                }
            }

            private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

            private static bool TryRecompress(PdfDictionary image, int quality, int maxSide)
            {
                try
                {
                    if (image.Stream?.Value is not { Length: > 0 } raw) return false;

                    var filter = Resolve(image.Elements["/Filter"]);
                    string? name = filter switch
                    {
                        PdfName n => n.Value,
                        PdfArray { Elements.Count: 1 } a => (Resolve(a.Elements[0]) as PdfName)?.Value,
                        _ => null,
                    };
                    bool isFlate = name == "/FlateDecode";
                    if (name != "/DCTDecode" && !isFlate) return false;

                    // Leave anything a re-encode could change the look of.
                    foreach (string key in new[] { "/DecodeParms", "/Decode", "/Mask", "/ImageMask", "/Alternates", "/OC" })
                        if (image.Elements.ContainsKey(key)) return false;
                    if (image.Elements.GetInteger("/BitsPerComponent") != 8) return false;

                    bool? gray = ColourSpaceIsGray(Resolve(image.Elements["/ColorSpace"]));
                    if (gray == null) return false;

                    // Scans and exported pages are often plain (Flate) pixels, not JPEGs: turn them into a JPEG, which is far smaller for photos and scans.
                    // Both kinds are decoded whole: ask the budget for the pixels first (and leave absurd sizes alone).
                    int declaredW = image.Elements.GetInteger("/Width"), declaredH = image.Elements.GetInteger("/Height");
                    if (declaredW <= 0 || declaredH <= 0 || (long)declaredW * declaredH > 120_000_000) return false;
                    using var budget = MemoryBudget.Acquire(ImageLoad.EstimateBytes((ulong)declaredW, (ulong)declaredH));
                    using var picture = isFlate ? ReadFlatePixels(image, gray.Value) : new MagickImage(raw);
                    if (picture == null || picture.ColorSpace == ImageMagick.ColorSpace.CMYK) return false;

                    uint width = picture.Width, height = picture.Height;
                    if (Math.Max(width, height) > maxSide)
                        picture.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide) { Greater = true });

                    picture.Strip();
                    picture.Settings.Interlace = Interlace.NoInterlace;   // baseline JPEG (Magick.NET 14: Interlace is set through Settings)
                    picture.ColorType = gray.Value ? ColorType.Grayscale : ColorType.TrueColor;
                    picture.Quality = (uint)quality;
                    picture.Format = MagickFormat.Jpeg;
                    using var ms = new MemoryStream();
                    picture.Write(ms);
                    byte[] smaller = ms.ToArray();
                    if (!WorthKeeping(raw.Length, smaller.Length, 0.10)) return false;

                    // The PDF says how many colour channels the picture has - the new one must match.
                    using (var check = new MagickImage(smaller))
                    {
                        bool checkGray = check.ColorSpace == ImageMagick.ColorSpace.Gray || check.ChannelCount == 1;
                        if (checkGray != gray.Value || check.Width != picture.Width || check.Height != picture.Height) return false;
                    }

                    image.Stream.Value = smaller;
                    if (isFlate) image.Elements.SetName("/Filter", "/DCTDecode");
                    image.Elements.SetInteger("/Length", smaller.Length);
                    image.Elements.SetInteger("/Width", (int)picture.Width);
                    image.Elements.SetInteger("/Height", (int)picture.Height);
                    return true;
                }
                catch (Exception)
                {
                    return false;   // any doubt: keep the original picture
                }
            }

            /// <summary>Decodes a plain 8-bit Flate picture into pixels, or null when its data is not exactly width x height x channels.</summary>
            private static MagickImage? ReadFlatePixels(PdfDictionary image, bool gray)
            {
                int w = image.Elements.GetInteger("/Width"), h = image.Elements.GetInteger("/Height");
                if (w <= 0 || h <= 0 || (long)w * h > 120_000_000) return null;
                byte[]? pixels = image.Stream?.UnfilteredValue;
                int channels = gray ? 1 : 3;
                if (pixels == null || pixels.LongLength != (long)w * h * channels) return null;
                var settings = new MagickReadSettings { Width = (uint)w, Height = (uint)h, Depth = 8, Format = gray ? MagickFormat.Gray : MagickFormat.Rgb };
                return new MagickImage(pixels, settings);
            }

            /// <summary>true = grey, false = RGB, null = something else (CMYK, indexed, Lab …) that's left alone.</summary>
            private static bool? ColourSpaceIsGray(PdfItem? space)
            {
                switch (space)
                {
                    case PdfName n when n.Value == "/DeviceGray":
                        return true;
                    case PdfName n when n.Value == "/DeviceRGB":
                        return false;
                    case PdfArray a when a.Elements.Count == 2 && (Resolve(a.Elements[0]) as PdfName)?.Value == "/ICCBased":
                        var profile = Resolve(a.Elements[1]) as PdfDictionary;
                        return profile?.Elements.GetInteger("/N") switch
                        {
                            1 => true,
                            3 => false,
                            _ => null,
                        };
                    default:
                        return null;
                }
            }
        }
    }
}