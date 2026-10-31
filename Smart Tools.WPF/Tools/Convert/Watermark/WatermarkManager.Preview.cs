using ImageMagick;
using Smart_Tools.WPF.Models;
using System.IO;
using System.Windows.Media.Imaging;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;
using WinPdfPageRenderOptions = Windows.Data.Pdf.PdfPageRenderOptions;
using WinStorageFile = Windows.Storage.StorageFile;
using WinStreams = Windows.Storage.Streams;

namespace Smart_Tools.WPF.Controllers
{
    // What the preview shows behind the mark: the picture itself, a frame of the video, or the first page of the PDF.
    // The mark is percentages of the file, so drawing it on this smaller picture looks the same as on the real file.
    public static partial class WatermarkManager
    {
        /// <summary>A frozen picture to show, and why not when there is none.</summary>
        public sealed record PreviewSource(BitmapSource? Bitmap, string? Problem);

        public static async Task<PreviewSource> LoadPreviewAsync(WatermarkItem item, int maxSide, CancellationToken token)
        {
            try
            {
                return item.Kind switch
                {
                    WatermarkFileKind.Image => new PreviewSource(await Task.Run(() => ImagePreview(item.FullPath, maxSide), token).ConfigureAwait(false), null),
                    WatermarkFileKind.Pdf => await PdfPreviewAsync(item.FullPath, maxSide, token).ConfigureAwait(false),
                    _ => await VideoPreviewAsync(item.FullPath, maxSide, token).ConfigureAwait(false),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new PreviewSource(null, FriendlyProblem(ex));
            }
        }

        private static BitmapSource ImagePreview(string path, int maxSide)
        {
            using var image = new MagickImage(path);
            image.AutoOrient();
            if (image.Width > maxSide || image.Height > maxSide) image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
            image.ColorSpace = ColorSpace.sRGB;
            using var ms = new MemoryStream();
            image.Write(ms, MagickFormat.Png);
            return FromBytes(ms.ToArray());
        }

        private static BitmapSource FromBytes(byte[] bytes)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>The first page as Windows draws it (page rotation included). A PDF Windows can't open gets a blank page of the right shape.</summary>
        private static async Task<PreviewSource> PdfPreviewAsync(string path, int maxSide, CancellationToken token)
        {
            try
            {
                var file = await WinStorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token).ConfigureAwait(false);
                var doc = await WinPdfDocument.LoadFromFileAsync(file).AsTask(token).ConfigureAwait(false);
                if (doc.PageCount == 0) return new PreviewSource(null, "This PDF has no pages.");
                using var page = doc.GetPage(0);
                double scale = maxSide / Math.Max(page.Size.Width, page.Size.Height);
                var options = new WinPdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
                    DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
                };
                using var stream = new WinStreams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, options).AsTask(token).ConfigureAwait(false);
                stream.Seek(0);
                using var reader = new WinStreams.DataReader(stream);
                await reader.LoadAsync((uint)stream.Size).AsTask(token).ConfigureAwait(false);
                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
                return new PreviewSource(FromBytes(bytes), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new PreviewSource(null, ex.HResult == unchecked((int)0x8007052B) || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                    ? "This PDF is protected with a password, so it can't be marked."
                    : "The first page couldn't be drawn for the preview.");
            }
        }

        /// <summary>A frame from one second in (or the first frame for a very short video).</summary>
        private static async Task<PreviewSource> VideoPreviewAsync(string path, int maxSide, CancellationToken token)
        {
            if (MissingVideoEngine() is { } missing) return new PreviewSource(null, missing);
            string work = Path.Combine(WorkRoot, "preview-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(work);
                string png = Path.Combine(work, "frame.png");
                foreach (string seek in new[] { "1", "0" })
                {
                    try
                    {
                        string scale = $"scale='min({maxSide},iw)':-2";
                        await ConverterManager.RunFfmpegAsync(new List<string> { "-y", "-nostdin", "-ss", seek, "-i", path, "-frames:v", "1", "-an", "-vf", scale, png }, 0, _ => { }, token).ConfigureAwait(false);
                        if (File.Exists(png) && new FileInfo(png).Length > 0) return new PreviewSource(FromBytes(await File.ReadAllBytesAsync(png, token).ConfigureAwait(false)), null);
                    }
                    catch (InvalidOperationException) when (seek == "1")
                    {
                        // a very short video has no frame at one second; try the first frame
                    }
                }
                return new PreviewSource(null, "No picture could be taken from this video.");
            }
            finally
            {
                try { Directory.Delete(work, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swept later */ }
            }
        }
    }
}
