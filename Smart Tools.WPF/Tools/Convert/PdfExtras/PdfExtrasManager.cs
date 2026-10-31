using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// PDF Extras - the logic: rotate pages, remove or keep pages, add page numbers, edit the document properties, and
    /// add or remove a password. One PDF at a time, always saved as a new file (never over the original), written under a
    /// temporary name and renamed at the end. Page ranges, file names and error wording come from <see cref="PdfToolkitManager"/>.
    /// </summary>
    public static partial class PdfExtrasManager
    {
        private const string FontFamily = "Verdana";   // WindowsFontResolver falls back to Arial, Tahoma or Segoe UI
        private const double NumberMargin = 28;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>Documents\Smart Tools\PDFs, the same place the other PDF tools save.</summary>
        public static string DefaultFolder => PdfToolkitManager.DefaultFolder;

        // =================================================================
        //  Reading the PDF that was chosen
        // =================================================================

        /// <summary>Page count, size, password state and properties. Never throws: a PDF that can't be read says why in <see cref="PdfExtraInfo.Problem"/>.</summary>
        public static PdfExtraInfo ReadInfo(string path, string? password = null)
        {
            long size = 0;
            try
            {
                size = new FileInfo(path).Length;
                string? pw = string.IsNullOrEmpty(password) ? null : password;
                using var doc = PdfReader.Open(path, pw, PdfDocumentOpenMode.InformationOnly);

                double width = 0, height = 0;
                if (doc.PageCount > 0)
                {
                    var page = doc.Pages[0];
                    bool sideways = ((page.Rotate % 360) + 360) % 360 is 90 or 270;
                    width = sideways ? page.Height.Point : page.Width.Point;
                    height = sideways ? page.Width.Point : page.Height.Point;
                }

                // PdfSharp decrypts as it reads, so it cannot say whether the file was encrypted; the file itself can.
                bool encrypted = HasEncryptEntry(path);

                // A PDF can open for reading yet refuse changes without its owner password.
                bool needsOwner = encrypted && CanModify(path, pw) == PdfAccess.NeedsOwner;

                return new PdfExtraInfo
                {
                    Path = path,
                    SizeBytes = size,
                    PageCount = doc.PageCount,
                    IsEncrypted = encrypted,
                    NeedsOwnerPassword = needsOwner,
                    Title = doc.Info.Title ?? "",
                    Author = doc.Info.Author ?? "",
                    Subject = doc.Info.Subject ?? "",
                    Keywords = doc.Info.Keywords ?? "",
                    PageWidth = width,
                    PageHeight = height,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var access = Classify(ex);
                bool password_ = access is PdfAccess.NeedsPassword or PdfAccess.WrongPassword;
                return new PdfExtraInfo
                {
                    Path = path,
                    SizeBytes = size,
                    NeedsPassword = access == PdfAccess.NeedsPassword,
                    IsEncrypted = password_ || access == PdfAccess.NeedsOwner,
                    Problem = access switch
                    {
                        PdfAccess.NeedsPassword => "Password",
                        PdfAccess.WrongPassword => "WrongPassword",
                        _ => PdfToolkitManager.PlainError(ex),
                    },
                };
            }
        }

        private enum PdfAccess { Ok, NeedsPassword, WrongPassword, NeedsOwner, Other }

        /// <summary>PdfSharp reports a password problem only in words; these are the three it uses.</summary>
        private static PdfAccess Classify(Exception ex)
        {
            string m = ex.Message;
            if (m.Contains("owner password is required", StringComparison.OrdinalIgnoreCase)) return PdfAccess.NeedsOwner;
            if (m.Contains("password is required", StringComparison.OrdinalIgnoreCase)) return PdfAccess.NeedsPassword;
            if (m.Contains("password is invalid", StringComparison.OrdinalIgnoreCase) || m.Contains("password was invalid", StringComparison.OrdinalIgnoreCase)) return PdfAccess.WrongPassword;
            return PdfAccess.Other;
        }

        /// <summary>Whether this password lets the PDF be changed (only encrypted PDFs need the check).</summary>
        private static PdfAccess CanModify(string path, string? password)
        {
            try
            {
                using var doc = PdfReader.Open(path, password, PdfDocumentOpenMode.Modify);
                return PdfAccess.Ok;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var access = Classify(ex);
                return access == PdfAccess.Other ? PdfAccess.Ok : access;   // other trouble is reported when the real run hits it
            }
        }

        /// <summary>
        /// Whether the file has an /Encrypt entry in its trailer (or cross-reference stream), which sits at the end of the
        /// file, or near the start in a "fast web view" file. Only those two ends are read.
        /// </summary>
        public static bool HasEncryptEntry(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return EncryptEntry.IsMatch(ReadEnd(stream, 0, 16 * 1024)) || EncryptEntry.IsMatch(ReadEnd(stream, Math.Max(0, stream.Length - 256 * 1024), 256 * 1024));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            static string ReadEnd(FileStream s, long from, int max)
            {
                s.Position = from;
                var buffer = new byte[(int)Math.Min(max, s.Length - from)];
                s.ReadExactly(buffer);
                return System.Text.Encoding.Latin1.GetString(buffer);
            }
        }

        private static readonly System.Text.RegularExpressions.Regex EncryptEntry = new(@"/Encrypt\s+\d+\s+\d+\s+R", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // =================================================================
        //  Names
        // =================================================================

        /// <summary>"Report.pdf" -> "Report_rotated.pdf". The word says what was done, so several results can sit in one folder.</summary>
        public static string OutputFileName(PdfExtraOperation op, PdfExtraPagesMode pagesMode, string sourcePath)
        {
            string word = op switch
            {
                PdfExtraOperation.Rotate => "rotated",
                PdfExtraOperation.Pages => pagesMode == PdfExtraPagesMode.Remove ? "trimmed" : "extract",
                PdfExtraOperation.PageNumbers => "numbered",
                PdfExtraOperation.Properties => "properties",
                PdfExtraOperation.Protect => "protected",
                PdfExtraOperation.Crop => "cropped",
                PdfExtraOperation.PageSize => "resized",
                PdfExtraOperation.Arrange => "arranged",
                _ => "unlocked",
            };
            return PdfToolkitManager.SafeName(Path.GetFileNameWithoutExtension(sourcePath)) + "_" + word + ".pdf";
        }

        /// <summary>The text printed on a page for a number.</summary>
        public static string NumberText(PdfExtraNumberFormat format, int number, int total, string pageWord = "Page", string ofWord = "of") => format switch
        {
            PdfExtraNumberFormat.Plain => number.ToString(CultureInfo.CurrentCulture),
            PdfExtraNumberFormat.Page => $"{pageWord} {number}",
            PdfExtraNumberFormat.PageOfTotal => $"{pageWord} {number} {ofWord} {total}",
            _ => $"{number} / {total}",
        };

        // =================================================================
        //  Applying
        // =================================================================

        /// <summary>Runs one operation on the PDF and saves the result as a new file. Does not throw: the outcome says what happened.</summary>
        public static PdfExtraResult Apply(PdfExtraRequest request, IProgress<double>? progress, CancellationToken token)
        {
            if (request.Operation == PdfExtraOperation.Arrange) return Arrange(request, token);

            string temp = "";
            try
            {
                long before = new FileInfo(request.SourcePath).Length;
                token.ThrowIfCancellationRequested();

                bool wasEncrypted = HasEncryptEntry(request.SourcePath);
                if (request.Operation == PdfExtraOperation.Unlock && !wasEncrypted)
                    return new PdfExtraResult { Outcome = PdfExtraOutcome.NothingToDo, SizeBefore = before };

                using var doc = PdfReader.Open(request.SourcePath, string.IsNullOrEmpty(request.OpenPassword) ? null : request.OpenPassword, PdfDocumentOpenMode.Modify);
                int pagesBefore = doc.PageCount;
                if (pagesBefore == 0) return new PdfExtraResult { Outcome = PdfExtraOutcome.Failed, Detail = "This PDF has no pages.", SizeBefore = before };

                var step = Change(doc, request, progress, token);
                if (step.Outcome != PdfExtraOutcome.Done)
                    return new PdfExtraResult { Outcome = step.Outcome, OutsideRanges = step.Outside, Detail = step.Detail, SizeBefore = before };

                Directory.CreateDirectory(request.OutputFolder);
                string stem = PdfToolkitManager.SafeName(Path.GetFileNameWithoutExtension(request.SourcePath));
                temp = Path.Combine(request.OutputFolder, $".{stem}.creating-{Guid.NewGuid():N}.tmp");

                token.ThrowIfCancellationRequested();
                using var replacement = step.Result;
                var toSave = replacement ?? doc;
                int pagesAfter = toSave.PageCount;   // PdfSharp refuses PageCount once the document is saved
                toSave.Save(temp);
                token.ThrowIfCancellationRequested();

                string final = Move(temp, Path.Combine(request.OutputFolder, OutputFileName(request.Operation, request.PagesMode, request.SourcePath)));
                temp = "";

                return new PdfExtraResult
                {
                    Outcome = PdfExtraOutcome.Done,
                    OutputPath = final,
                    SizeBefore = before,
                    SizeAfter = new FileInfo(final).Length,
                    PagesAffected = step.Affected,
                    PagesAfter = pagesAfter,
                    OutsideRanges = step.Outside,

                    // PdfSharp decrypts as it reads, so a protected PDF is saved without its password unless Protect sets a new one.
                    ProtectionDropped = wasEncrypted && request.Operation is not (PdfExtraOperation.Protect or PdfExtraOperation.Unlock),
                };
            }
            catch (OperationCanceledException)
            {
                return new PdfExtraResult { Outcome = PdfExtraOutcome.Canceled };
            }
            catch (Exception ex)
            {
                return Classify(ex) switch
                {
                    PdfAccess.NeedsPassword => new PdfExtraResult { Outcome = PdfExtraOutcome.NeedsPassword },
                    PdfAccess.WrongPassword => new PdfExtraResult { Outcome = PdfExtraOutcome.WrongPassword },
                    PdfAccess.NeedsOwner => new PdfExtraResult { Outcome = PdfExtraOutcome.NeedsOwnerPassword },
                    _ => new PdfExtraResult { Outcome = PdfExtraOutcome.Failed, Detail = PdfToolkitManager.PlainError(ex) },
                };
            }
            finally
            {
                if (temp.Length > 0)
                {
                    try { File.Delete(temp); } catch (IOException) { /* a leftover temp file is swept by the next run's unique naming */ } catch (UnauthorizedAccessException) { }
                }
            }
        }

        /// <summary>Renames the finished temp file to a name nobody has, trying again if one appears meanwhile.</summary>
        private static string Move(string temp, string wanted)
        {
            for (int attempt = 0; ; attempt++)
            {
                string target = PdfToolkitManager.UniquePath(wanted);
                try
                {
                    File.Move(temp, target, overwrite: false);
                    return target;
                }
                catch (IOException) when (File.Exists(target) && attempt < 20)
                {
                    // Another program took the name between the check and the move.
                }
            }
        }

        /// <summary>The outcome of one change. <see cref="Result"/> is a new document to save instead of the one that was opened.</summary>
        private readonly record struct Step(PdfExtraOutcome Outcome, int Affected, List<string> Outside, string Detail = "", PdfDocument? Result = null);

        private static Step Done(int affected, List<string>? outside = null, PdfDocument? result = null) => new(PdfExtraOutcome.Done, affected, outside ?? new List<string>(), "", result);
        private static Step Stop(PdfExtraOutcome outcome, List<string>? outside = null, string detail = "") => new(outcome, 0, outside ?? new List<string>(), detail);

        private static Step Change(PdfDocument doc, PdfExtraRequest r, IProgress<double>? progress, CancellationToken token) => r.Operation switch
        {
            PdfExtraOperation.Rotate => Rotate(doc, r, token),
            PdfExtraOperation.Pages => ChangePages(doc, r),
            PdfExtraOperation.PageNumbers => AddNumbers(doc, r, progress, token),
            PdfExtraOperation.Properties => SetProperties(doc, r),
            PdfExtraOperation.Protect => Protect(doc, r),
            PdfExtraOperation.Crop => Crop(doc, r, token),
            PdfExtraOperation.PageSize => FitToPaper(doc, r, token),
            _ => Unlock(doc),
        };

        // =================================================================
        //  Pages
        // =================================================================

        /// <summary>
        /// The pages (0-based) a typed box means. Blank = every page when <paramref name="blankMeansAll"/>. Null when
        /// the text can't be used; <paramref name="outside"/> lists ranges that start past the end.
        /// </summary>
        public static HashSet<int>? PagesFromText(string? text, int pageCount, bool blankMeansAll, out List<string> outside)
        {
            outside = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return blankMeansAll ? Enumerable.Range(0, pageCount).ToHashSet() : null;

            var parse = PdfToolkitManager.ParseRanges(text);
            if (!parse.IsValid) return null;

            var (ranges, past) = PdfToolkitManager.ResolveRanges(parse.Ranges, pageCount);
            outside = past;
            var pages = new HashSet<int>();
            foreach (var (start, end) in ranges)
                for (int p = start; p <= end; p++) pages.Add(p - 1);
            return pages.Count == 0 ? null : pages;
        }

        private static Step Rotate(PdfDocument doc, PdfExtraRequest r, CancellationToken token)
        {
            var pages = PagesFromText(r.Pages, doc.PageCount, blankMeansAll: true, out var outside);
            if (pages == null) return Stop(PdfExtraOutcome.BadPages, outside);

            int degrees = ((r.Degrees % 360) + 360) % 360;
            if (degrees == 0) return Stop(PdfExtraOutcome.NothingToDo);

            foreach (int i in pages)
            {
                token.ThrowIfCancellationRequested();
                var page = doc.Pages[i];
                page.Rotate = (((page.Rotate + degrees) % 360) + 360) % 360;
            }
            return Done(pages.Count, outside);
        }

        private static Step ChangePages(PdfDocument doc, PdfExtraRequest r)
        {
            var typed = PagesFromText(r.Pages, doc.PageCount, blankMeansAll: false, out var outside);
            if (typed == null) return Stop(PdfExtraOutcome.BadPages, outside);

            var remove = r.PagesMode == PdfExtraPagesMode.Remove
                ? typed
                : Enumerable.Range(0, doc.PageCount).Where(i => !typed.Contains(i)).ToHashSet();

            if (remove.Count == 0) return Stop(PdfExtraOutcome.NothingToDo, outside);
            if (remove.Count >= doc.PageCount) return Stop(PdfExtraOutcome.NoPageLeft, outside);

            foreach (int i in remove.OrderByDescending(i => i)) doc.Pages.RemoveAt(i);
            return Done(remove.Count, outside);
        }

        // =================================================================
        //  Arrange pages
        // =================================================================

        /// <summary>True when the plan is the PDF as it is: every page once, in order, not turned, nothing added.</summary>
        public static bool IsUnchanged(IReadOnlyList<PdfPagePlan> plan, int pageCount) =>
            plan.Count == pageCount && plan.Select((p, i) => p.Source == 0 && p.PageIndex == i && ((p.ExtraRotation % 360) + 360) % 360 == 0).All(ok => ok);

        /// <summary>
        /// Builds a new PDF from a page plan: pages of the PDF in the order given (a page can appear twice), pages taken
        /// from other PDFs, and blank pages. Bookmarks, links and form fields are not carried over: only the pages are.
        /// The document details (title, author, subject, keywords) come from the PDF being arranged.
        /// </summary>
        private static PdfExtraResult Arrange(PdfExtraRequest r, CancellationToken token)
        {
            string temp = "";
            var sources = new List<PdfDocument>();
            try
            {
                long before = new FileInfo(r.SourcePath).Length;
                if (r.Plan.Count == 0) return new PdfExtraResult { Outcome = PdfExtraOutcome.NoPageLeft, SizeBefore = before };

                string? password = string.IsNullOrEmpty(r.OpenPassword) ? null : r.OpenPassword;
                bool wasEncrypted = HasEncryptEntry(r.SourcePath);
                foreach (string path in new[] { r.SourcePath }.Concat(r.ExtraSources))
                {
                    token.ThrowIfCancellationRequested();
                    sources.Add(PdfReader.Open(path, password, PdfDocumentOpenMode.Import));
                }

                if (r.Plan.Any(p => !p.IsBlank && (p.Source >= sources.Count || p.PageIndex < 0 || p.PageIndex >= sources[p.Source].PageCount)))
                    return new PdfExtraResult { Outcome = PdfExtraOutcome.Failed, SizeBefore = before, Detail = "A page in the new order isn't in its PDF any more." };
                if (IsUnchanged(r.Plan, sources[0].PageCount) && r.ExtraSources.Count == 0)
                    return new PdfExtraResult { Outcome = PdfExtraOutcome.NothingToDo, SizeBefore = before };

                using var output = new PdfDocument();
                var info = sources[0].Info;
                output.Info.Title = info.Title; output.Info.Author = info.Author; output.Info.Subject = info.Subject; output.Info.Keywords = info.Keywords;

                foreach (var step in r.Plan)
                {
                    token.ThrowIfCancellationRequested();
                    PdfPage page;
                    if (step.IsBlank)
                    {
                        page = output.AddPage();
                        page.Width = XUnit.FromPoint(Math.Max(step.Width, 36));
                        page.Height = XUnit.FromPoint(Math.Max(step.Height, 36));
                        continue;
                    }
                    page = output.AddPage(sources[step.Source].Pages[step.PageIndex]);
                    int extra = ((step.ExtraRotation % 360) + 360) % 360;
                    if (extra != 0) page.Rotate = (((page.Rotate + extra) % 360) + 360) % 360;
                }

                Directory.CreateDirectory(r.OutputFolder);
                string stem = PdfToolkitManager.SafeName(Path.GetFileNameWithoutExtension(r.SourcePath));
                temp = Path.Combine(r.OutputFolder, $".{stem}.creating-{Guid.NewGuid():N}.tmp");
                int pagesAfter = output.PageCount;
                output.Save(temp);
                token.ThrowIfCancellationRequested();

                string final = Move(temp, Path.Combine(r.OutputFolder, OutputFileName(PdfExtraOperation.Arrange, PdfExtraPagesMode.Remove, r.SourcePath)));
                temp = "";
                return new PdfExtraResult
                {
                    Outcome = PdfExtraOutcome.Done,
                    OutputPath = final,
                    SizeBefore = before,
                    SizeAfter = new FileInfo(final).Length,
                    PagesAffected = r.Plan.Count,
                    PagesAfter = pagesAfter,
                    ProtectionDropped = wasEncrypted,
                };
            }
            catch (OperationCanceledException)
            {
                return new PdfExtraResult { Outcome = PdfExtraOutcome.Canceled };
            }
            catch (Exception ex)
            {
                return Classify(ex) switch
                {
                    PdfAccess.NeedsPassword => new PdfExtraResult { Outcome = PdfExtraOutcome.NeedsPassword },
                    PdfAccess.WrongPassword => new PdfExtraResult { Outcome = PdfExtraOutcome.WrongPassword },
                    PdfAccess.NeedsOwner => new PdfExtraResult { Outcome = PdfExtraOutcome.NeedsOwnerPassword },
                    _ => new PdfExtraResult { Outcome = PdfExtraOutcome.Failed, Detail = PdfToolkitManager.PlainError(ex) },
                };
            }
            finally
            {
                foreach (var source in sources) source.Dispose();
                if (temp.Length > 0)
                {
                    try { File.Delete(temp); } catch (IOException) { /* a leftover temp file is not worth an error */ } catch (UnauthorizedAccessException) { }
                }
            }
        }

        // =================================================================
        //  Crop and page size
        // =================================================================

        private const double PointsPerMillimeter = 72.0 / 25.4;
        private const double MinimumPageSide = 20;   // points: a crop that leaves less than this is a mistake

        /// <summary>
        /// The visible box after hiding margins given as the page is shown (left, top, right, bottom, in points).
        /// <paramref name="box"/> is in the page's own space (PDF: origin bottom left) and <paramref name="rotation"/> is the page's
        /// /Rotate, so the shown left edge is a different edge of the box for each quarter turn. Null when nothing is left.
        /// </summary>
        public static (double X1, double Y1, double X2, double Y2)? CropBoxFor((double X1, double Y1, double X2, double Y2) box, int rotation, double left, double top, double right, double bottom)
        {
            var (x1, y1, x2, y2) = box;
            switch (((rotation % 360) + 360) % 360)
            {
                case 90:    // shown left = own bottom, top = own left, right = own top, bottom = own right
                    y1 += left; x1 += top; y2 -= right; x2 -= bottom;
                    break;
                case 180:   // shown left = own right, top = own bottom, right = own left, bottom = own top
                    x2 -= left; y1 += top; x1 += right; y2 -= bottom;
                    break;
                case 270:   // shown left = own top, top = own right, right = own bottom, bottom = own left
                    y2 -= left; x2 -= top; y1 += right; x1 += bottom;
                    break;
                default:
                    x1 += left; y2 -= top; x2 -= right; y1 += bottom;
                    break;
            }
            return x2 - x1 < MinimumPageSide || y2 - y1 < MinimumPageSide ? null : (x1, y1, x2, y2);
        }

        private static Step Crop(PdfDocument doc, PdfExtraRequest r, CancellationToken token)
        {
            var pages = PagesFromText(r.Pages, doc.PageCount, blankMeansAll: true, out var outside);
            if (pages == null) return Stop(PdfExtraOutcome.BadPages, outside);
            if (r.CropTop <= 0 && r.CropRight <= 0 && r.CropBottom <= 0 && r.CropLeft <= 0) return Stop(PdfExtraOutcome.NothingToDo);

            double l = r.CropLeft * PointsPerMillimeter, t = r.CropTop * PointsPerMillimeter, rt = r.CropRight * PointsPerMillimeter, b = r.CropBottom * PointsPerMillimeter;

            // Work out every page first, so one page that would be left empty stops the run before anything changes.
            var boxes = new List<(PdfPage Page, (double, double, double, double) Box)>();
            foreach (int i in pages.OrderBy(i => i))
            {
                token.ThrowIfCancellationRequested();
                var page = doc.Pages[i];
                var shown = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
                var result = CropBoxFor((shown.X1, shown.Y1, shown.X2, shown.Y2), page.Rotate, l, t, rt, b);
                if (result is not { } box) return Stop(PdfExtraOutcome.CropTooBig, detail: (i + 1).ToString(CultureInfo.InvariantCulture));
                boxes.Add((page, box));
            }

            foreach (var (page, (x1, y1, x2, y2)) in boxes) page.CropBox = new PdfRectangle(new XPoint(x1, y1), new XPoint(x2, y2));
            return Done(boxes.Count, outside);
        }

        /// <summary>Portrait size of a paper in points.</summary>
        public static (double Width, double Height) PaperSize(PdfExtraPaper paper) => paper switch
        {
            PdfExtraPaper.A5 => (419.53, 595.28),
            PdfExtraPaper.A3 => (841.89, 1190.55),
            PdfExtraPaper.Letter => (612, 792),
            PdfExtraPaper.Legal => (612, 1008),
            _ => (595.28, 841.89),
        };

        /// <summary>
        /// The size a page of <paramref name="width"/> by <paramref name="height"/> gets on a paper, and the scale of its
        /// content: the paper is turned to match the page (a landscape page stays landscape) and the content is made as
        /// large as fits, centered, never distorted.
        /// </summary>
        public static (double PageWidth, double PageHeight, double Scale, double OffsetX, double OffsetY) FitOnPaper(double width, double height, PdfExtraPaper paper)
        {
            var (pw, ph) = PaperSize(paper);
            if (width > height) (pw, ph) = (ph, pw);
            double scale = Math.Min(pw / width, ph / height);
            return (pw, ph, scale, (pw - width * scale) / 2, (ph - height * scale) / 2);
        }

        /// <summary>
        /// The matrix (a b c d e f of a PDF <c>cm</c>) that carries a page's own space onto a new page: first the page is
        /// turned the way it is shown (<paramref name="rotation"/>), then scaled by <paramref name="scale"/> and moved by
        /// the offsets. <paramref name="box"/> is the visible box in the page's own space.
        /// </summary>
        public static (double A, double B, double C, double D, double E, double F) FitMatrix((double X1, double Y1, double X2, double Y2) box, int rotation, double scale, double offsetX, double offsetY)
        {
            double w = box.X2 - box.X1, h = box.Y2 - box.Y1, s = scale;
            return (((rotation % 360) + 360) % 360) switch
            {
                90 => (0, -s, s, 0, offsetX - s * box.Y1, offsetY + s * (w + box.X1)),
                180 => (-s, 0, 0, -s, offsetX + s * (w + box.X1), offsetY + s * (h + box.Y1)),
                270 => (0, s, -s, 0, offsetX + s * (h + box.Y1), offsetY - s * box.X1),
                _ => (s, 0, 0, s, offsetX - s * box.X1, offsetY - s * box.Y1),
            };
        }

        /// <summary>
        /// Puts every page (or the typed ones) on the chosen paper, scaled to fit. The page keeps its own content: it is
        /// wrapped in one scale-and-turn matrix and clipped to what was visible, so text stays text and bookmarks still
        /// work. Links and form fields on the changed pages are removed, because they would sit in the wrong place.
        /// </summary>
        private static Step FitToPaper(PdfDocument doc, PdfExtraRequest r, CancellationToken token)
        {
            var pages = PagesFromText(r.Pages, doc.PageCount, blankMeansAll: true, out var outside);
            if (pages == null) return Stop(PdfExtraOutcome.BadPages, outside);

            foreach (int i in pages.OrderBy(i => i))
            {
                token.ThrowIfCancellationRequested();
                var page = doc.Pages[i];
                var shown = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
                var box = (shown.X1, shown.Y1, shown.X2, shown.Y2);
                int rotation = ((page.Rotate % 360) + 360) % 360;
                bool sideways = rotation is 90 or 270;
                double w = box.Item3 - box.Item1, h = box.Item4 - box.Item2;
                double displayWidth = sideways ? h : w, displayHeight = sideways ? w : h;
                if (displayWidth < 1 || displayHeight < 1) continue;

                var fit = FitOnPaper(displayWidth, displayHeight, r.Paper);
                var (a, b, c, d, e, f) = FitMatrix(box, rotation, fit.Scale, fit.OffsetX, fit.OffsetY);

                string before = string.Create(CultureInfo.InvariantCulture, $"q {a:0.#####} {b:0.#####} {c:0.#####} {d:0.#####} {e:0.#####} {f:0.#####} cm {box.Item1:0.#####} {box.Item2:0.#####} {w:0.#####} {h:0.#####} re W n\n");
                WrapContent(doc, page, before, "Q\n");

                foreach (string key in new[] { "/CropBox", "/TrimBox", "/BleedBox", "/ArtBox", "/Annots" }) page.Elements.Remove(key);
                page.Rotate = 0;
                page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(fit.PageWidth, fit.PageHeight));
            }
            return Done(pages.Count, outside);
        }

        /// <summary>Adds a content stream before and one after the page's own content (the page's streams are not touched).</summary>
        private static void WrapContent(PdfDocument doc, PdfPage page, string before, string after)
        {
            PdfSharp.Pdf.Advanced.PdfReference Stream(string text)
            {
                var dict = new PdfDictionary(doc);
                dict.CreateStream(System.Text.Encoding.ASCII.GetBytes(text));
                doc.Internals.AddObject(dict);
                return dict.ReferenceNotNull;
            }

            var elements = page.Contents.Elements;
            elements.Insert(0, Stream(before));
            elements.Add(Stream(after));
        }
        // =================================================================
        //  Page numbers
        // =================================================================

        private static Step AddNumbers(PdfDocument doc, PdfExtraRequest r, IProgress<double>? progress, CancellationToken token)
        {
            ImageToPdfManager.EnsureFonts();
            var font = new XFont(FontFamily, Math.Clamp(r.FontSize, 6, 48), XFontStyleEx.Regular);

            int first = r.SkipFirstPage ? 1 : 0;
            int numbered = doc.PageCount - first;
            if (numbered <= 0) return Stop(PdfExtraOutcome.NothingToDo);
            int total = r.StartNumber + numbered - 1;

            for (int i = first; i < doc.PageCount; i++)
            {
                token.ThrowIfCancellationRequested();
                var page = doc.Pages[i];

                // The part of the page that is shown: the crop box when there is one, else the whole media box
                // (reading CropBox on a page without one would add it and change the page).
                var media = page.MediaBox;
                var crop = page.Elements.ContainsKey("/CropBox") ? page.CropBox : media;
                bool hasCrop = !crop.IsEmpty && crop.Width > 0 && crop.Height > 0 && (crop.X1 != media.X1 || crop.Y1 != media.Y1 || crop.X2 != media.X2 || crop.Y2 != media.Y2);
                double pw = hasCrop ? crop.Width : media.Width, ph = hasCrop ? crop.Height : media.Height;
                double shiftX = hasCrop ? crop.X1 - media.X1 : 0, shiftY = hasCrop ? media.Y2 - crop.Y2 : 0;

                int rotation = ((page.Rotate % 360) + 360) % 360;
                bool sideways = rotation is 90 or 270;
                double dw = sideways ? ph : pw, dh = sideways ? pw : ph;   // as shown on screen
                if (dw < 1 || dh < 1) continue;

                string text = NumberText(r.Format, r.StartNumber + i - first, total, r.PageWord, r.OfWord);

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                gfx.TranslateTransform(shiftX, shiftY);
                // Shown (x, y) -> the page's own space. 90 and 270 turn the page on its side.
                switch (rotation)
                {
                    case 90: gfx.MultiplyTransform(new XMatrix(0, -1, 1, 0, 0, dw), XMatrixOrder.Prepend); break;
                    case 180: gfx.MultiplyTransform(new XMatrix(-1, 0, 0, -1, dw, dh), XMatrixOrder.Prepend); break;
                    case 270: gfx.MultiplyTransform(new XMatrix(0, 1, -1, 0, dh, 0), XMatrixOrder.Prepend); break;
                }

                var (x, y) = NumberPoint(r.Position, dw, dh, gfx.MeasureString(text, font).Width, font.GetHeight());
                gfx.DrawString(text, font, XBrushes.DimGray, new XPoint(x, y), XStringFormats.TopLeft);
                progress?.Report((i - first + 1) * 100.0 / numbered);
            }
            return Done(numbered);
        }

        /// <summary>Where the number's top-left corner goes on a page shown <paramref name="width"/> by <paramref name="height"/>.</summary>
        public static (double X, double Y) NumberPoint(PdfExtraNumberPosition position, double width, double height, double textWidth, double textHeight)
        {
            double margin = Math.Min(NumberMargin, Math.Min(width, height) / 4);
            double x = position switch
            {
                PdfExtraNumberPosition.BottomLeft or PdfExtraNumberPosition.TopLeft => margin,
                PdfExtraNumberPosition.BottomRight or PdfExtraNumberPosition.TopRight => width - margin - textWidth,
                _ => (width - textWidth) / 2,
            };
            double y = position is PdfExtraNumberPosition.TopLeft or PdfExtraNumberPosition.TopCenter or PdfExtraNumberPosition.TopRight
                ? margin
                : height - margin - textHeight;
            return (x, y);
        }

        // =================================================================
        //  Properties
        // =================================================================

        /// <summary>
        /// Removes every entry of the Info dictionary (title, author, creator, dates, custom entries) and the hidden
        /// metadata streams (XMP, Adobe PieceInfo) of the document and its pages. PdfSharp writes its own Producer, Creator
        /// and XMP packet again on save, so the name of this tool stays in the new file; nothing of the original does.
        /// </summary>
        private static Step CleanDetails(PdfDocument doc)
        {
            int removed = 0;
            foreach (string key in doc.Info.Elements.Keys.ToList())
            {
                doc.Info.Elements.Remove(key);
                removed++;
            }

            removed += DropHidden(doc, doc.Internals.Catalog);
            foreach (PdfPage page in doc.Pages) removed += DropHidden(doc, page);
            return removed > 0 ? Done(0) : Stop(PdfExtraOutcome.NothingToDo);
        }

        private static int DropHidden(PdfDocument doc, PdfDictionary owner)
        {
            int removed = 0;
            foreach (string key in new[] { "/Metadata", "/PieceInfo" })
            {
                if (!owner.Elements.ContainsKey(key)) continue;
                if (owner.Elements.GetObject(key) is { IsIndirect: true } hidden) doc.Internals.RemoveObject(hidden);
                owner.Elements.Remove(key);
                removed++;
            }
            return removed;
        }

        private static Step SetProperties(PdfDocument doc, PdfExtraRequest r)
        {
            if (r.CleanAll) return CleanDetails(doc);

            bool changed = false;
            changed |= Set(doc, "/Title", doc.Info.Title, r.Title);
            changed |= Set(doc, "/Author", doc.Info.Author, r.Author);
            changed |= Set(doc, "/Subject", doc.Info.Subject, r.Subject);
            changed |= Set(doc, "/Keywords", doc.Info.Keywords, r.Keywords);
            return changed ? Done(0) : Stop(PdfExtraOutcome.NothingToDo);

            static bool Set(PdfDocument d, string key, string? current, string wanted)
            {
                wanted = wanted.Trim();
                if (string.Equals(current ?? "", wanted, StringComparison.Ordinal)) return false;
                if (wanted.Length == 0) d.Info.Elements.Remove(key);
                else d.Info.Elements.SetString(key, wanted);
                return true;
            }
        }

        // =================================================================
        //  Passwords
        // =================================================================

        private static Step Protect(PdfDocument doc, PdfExtraRequest r)
        {
            bool restricted = !r.AllowPrint || !r.AllowCopy || !r.AllowEdit;
            if (r.NewUserPassword.Length == 0 && !restricted) return Stop(PdfExtraOutcome.NothingToDo);

            // Restrictions only hold when there is an owner password to lift them; without one a random one is used and forgotten.
            string owner = r.NewOwnerPassword.Length > 0 ? r.NewOwnerPassword : RandomPassword();

            var security = doc.SecuritySettings;
            security.UserPassword = r.NewUserPassword;
            security.OwnerPassword = owner;
            security.PermitPrint = r.AllowPrint;
            security.PermitFullQualityPrint = r.AllowPrint;
            security.PermitExtractContent = r.AllowCopy;
            security.PermitModifyDocument = r.AllowEdit;
            security.PermitAnnotations = r.AllowEdit;
            security.PermitFormsFill = r.AllowEdit;
            security.PermitAssembleDocument = r.AllowEdit;
            doc.SecurityHandler.SetEncryptionToV4UsingAES();   // AES-128, understood by every current PDF viewer
            return Done(0);
        }

        private static Step Unlock(PdfDocument doc)
        {
            doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
            return Done(0);
        }

        private static string RandomPassword()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            return string.Create(24, 0, (span, _) =>
            {
                for (int i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            });
        }

        // =================================================================
        //  Settings
        // =================================================================

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "pdf_extras.json");

        public static PdfExtraSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new PdfExtraSettings();
                var s = JsonSerializer.Deserialize<PdfExtraSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new PdfExtraSettings();
                if (!Enum.IsDefined(s.Operation)) s.Operation = PdfExtraOperation.Rotate;
                if (!Enum.IsDefined(s.PagesMode)) s.PagesMode = PdfExtraPagesMode.Remove;
                if (!Enum.IsDefined(s.Position)) s.Position = PdfExtraNumberPosition.BottomCenter;
                if (!Enum.IsDefined(s.Format)) s.Format = PdfExtraNumberFormat.PageOfTotal;
                if (s.Degrees is not (90 or 180 or 270)) s.Degrees = 90;
                s.StartNumber = Math.Clamp(s.StartNumber, 0, 99999);
                s.FontSize = Math.Clamp(s.FontSize, 6, 48);
                return s;
            }
            catch (Exception)
            {
                return new PdfExtraSettings();
            }
        }

        public static void SaveSettings(PdfExtraSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                string temp = SettingsPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temp, SettingsPath, overwrite: true);
            }
            catch (Exception)
            {
                // Remembering the setup is a convenience; losing it is not worth an error.
            }
        }
    }
}
